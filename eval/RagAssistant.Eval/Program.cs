using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Chat;
using RagAssistant.Application.Ingestion;
using RagAssistant.Infrastructure;
using RagAssistant.Infrastructure.Ingestion;

// RAG evaluation harness. Talks to a running API over HTTP (the same path a user takes):
//   dotnet run --project eval/RagAssistant.Eval -- [--api http://localhost:5080] [--k 6] [--golden eval/golden-set.json]
// Faithfulness is judged by the LLM configured through Llm__* environment variables (same keys as the API).
// Utility: `dump <file.pdf>` prints the cleaned text of each page, used to write the golden set.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length >= 2 && args[0] == "dump")
{
    await using var pdf = File.OpenRead(args[1]);
    foreach (var page in new PdfPigTextExtractor().Extract(pdf))
        Console.WriteLine($"=== page {page.PageNumber} ===\n{TextCleaner.Clean(page.Text)}\n");
    return 0;
}

var options = Args.Parse(args);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var golden = JsonSerializer.Deserialize<List<GoldenItem>>(await File.ReadAllTextAsync(options.GoldenPath), json)!;

using var http = new HttpClient { BaseAddress = new Uri(options.Api), Timeout = TimeSpan.FromMinutes(3) };
var login = await http.PostAsJsonAsync("/api/auth/login", new { email = options.Email, password = options.Password });
login.EnsureSuccessStatusCode();
var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

var info = await http.GetFromJsonAsync<JsonElement>("/api/info");
var documents = await http.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", json) ?? [];
var missing = golden.Select(g => g.Document).Where(d => d is not null).Distinct()
    .Where(d => documents.All(x => x.FileName != d || x.Status != "Ready")).ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"These documents are not uploaded/ready for {options.Email}: {string.Join(", ", missing)}");
    return 1;
}

// Judge model: built from the same configuration keys as the API (Llm__Provider, Llm__Anthropic__ApiKey, ...).
var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = "Host=unused" })
    .AddEnvironmentVariables().Build();
var services = new ServiceCollection().AddLogging().AddInfrastructure(config).BuildServiceProvider();
var judge = services.GetRequiredService<IAuxiliaryChatModel>();
var canJudge = judge.Provider != "Mock";

var results = new List<ItemResult>();
foreach (var item in golden)
{
    Console.Write($"[{item.Id}] {item.Question[..Math.Min(70, item.Question.Length)]}… ");

    // 1. Retrieval hit@k: does any of the top-k chunks contain the gold page of the gold document?
    var search = await Send(() => http.PostAsJsonAsync("/api/search", new { query = item.Question, topK = options.K }));
    var chunks = (await search.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chunks").EnumerateArray().ToList();
    int? hitRank = null;
    for (var i = 0; i < chunks.Count; i++)
    {
        var c = chunks[i];
        if (item.Answerable && c.GetProperty("fileName").GetString() == item.Document &&
            item.Pages.Any(p => p >= c.GetProperty("pageStart").GetInt32() && p <= c.GetProperty("pageEnd").GetInt32()))
        {
            hitRank = i + 1;
            break;
        }
    }

    // 2. Full answer through the streaming endpoint.
    var chat = await Send(() => http.PostAsJsonAsync("/api/chat/stream", new { question = item.Question }));
    var (answer, citations, answerFound) = ParseStream(await chat.Content.ReadAsStringAsync());

    // 3. Citation correctness: at least one citation names the gold document and a page within ±1 of a gold page.
    var citationCorrect = item.Answerable && citations.Any(c =>
        c.FileName == item.Document && item.Pages.Any(p => Math.Abs(p - c.Page) <= 1));

    // 4. Faithfulness (LLM-as-judge): is every claim in the answer supported by the cited passages?
    double? faithfulness = null;
    if (canJudge && answerFound && citations.Count > 0)
        faithfulness = await Judge(judge, item.Question, answer, citations);

    var result = new ItemResult(item, hitRank, answerFound, citationCorrect, faithfulness, citations.Count, answer);
    results.Add(result);
    Console.WriteLine(item.Answerable
        ? $"hit@{options.K}={(hitRank is null ? "no" : $"yes(rank {hitRank})")} cite={(citationCorrect ? "ok" : "miss")} faith={faithfulness?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a"}"
        : $"abstained={(!answerFound ? "yes" : "NO")}");
}

var report = Report.Build(results, options, info, canJudge);
Console.WriteLine();
Console.WriteLine(report);
Directory.CreateDirectory(Path.GetDirectoryName(options.OutPath)!);
await File.WriteAllTextAsync(options.OutPath, report);
await File.WriteAllTextAsync(Path.ChangeExtension(options.OutPath, ".json"), JsonSerializer.Serialize(results, json));
Console.WriteLine($"Written to {options.OutPath}");
return 0;

// Retries on 429 (the API rate-limits chat and search per user).
async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> call)
{
    for (var attempt = 0; ; attempt++)
    {
        var res = await call();
        if (res.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 10)
        {
            res.EnsureSuccessStatusCode();
            return res;
        }
        var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
        Console.Write($"(rate limited, waiting {wait.TotalSeconds:0}s) ");
        await Task.Delay(wait + TimeSpan.FromSeconds(1));
    }
}

static (string Answer, List<CitationInfo> Citations, bool AnswerFound) ParseStream(string body)
{
    var answer = new StringBuilder();
    var citations = new List<CitationInfo>();
    var found = false;
    foreach (var block in body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
    {
        var data = block.Split('\n').FirstOrDefault(l => l.StartsWith("data:"))?[5..].Trim();
        if (data is null) continue;
        var e = JsonDocument.Parse(data).RootElement;
        switch (e.GetProperty("type").GetString())
        {
            case "token": answer.Append(e.GetProperty("text").GetString()); break;
            case "citations":
                citations.AddRange(e.GetProperty("citations").EnumerateArray().Select(c => new CitationInfo(
                    c.GetProperty("fileName").GetString()!, c.GetProperty("page").GetInt32(), c.GetProperty("snippet").GetString()!)));
                break;
            case "done": found = e.GetProperty("answerFound").GetBoolean(); break;
            case "error": throw new InvalidOperationException(e.GetProperty("message").GetString());
        }
    }
    return (answer.ToString(), citations, found);
}

static async Task<double?> Judge(IChatModel judge, string question, string answer, List<CitationInfo> citations)
{
    var context = string.Join("\n\n", citations.DistinctBy(c => (c.FileName, c.Page))
        .Select(c => $"<passage file=\"{c.FileName}\" page=\"{c.Page}\">\n{PromptBuilder.Escape(c.Snippet)}\n</passage>"));
    var prompt = new ChatPrompt(
        $"""
        {PromptBuilder.JudgeTaskMarker}
        You grade answers of a document question-answering system for faithfulness.
        Faithfulness means every factual claim in the answer is supported by the passages. Ignore citation markers like [doc1:3].
        Do not use outside knowledge, and do not judge completeness or style.
        Score: 1.0 = all claims supported, 0.5 = some claims unsupported, 0.0 = mostly unsupported or contradicted.
        Reply with JSON only: {"{"}"score": <number between 0 and 1>, "reason": "<one sentence>"{"}"}
        """,
        [new PromptMessage(PromptRole.User, $"<passages>\n{context}\n</passages>\n\n<question>{question}</question>\n\n<answer>\n{answer}\n</answer>")]);

    var completion = await judge.CompleteAsync(prompt, 300, CancellationToken.None);
    var text = completion.Text;
    var start = text.IndexOf('{');
    var end = text.LastIndexOf('}');
    if (start < 0 || end <= start) return null;
    try
    {
        var score = JsonDocument.Parse(text[start..(end + 1)]).RootElement.GetProperty("score");
        return score.ValueKind == JsonValueKind.Number ? Math.Clamp(score.GetDouble(), 0, 1) : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

internal sealed record GoldenItem(string Id, string Question, string? Document, int[] Pages, string? ReferenceAnswer)
{
    public bool Answerable => Document is not null;
}

internal sealed record DocumentInfo(Guid Id, string FileName, string Status);
internal sealed record CitationInfo(string FileName, int Page, string Snippet);
internal sealed record ItemResult(GoldenItem Item, int? HitRank, bool AnswerFound, bool CitationCorrect, double? Faithfulness,
    int CitationCount, string Answer);

internal sealed record Args(string Api, string Email, string Password, int K, string GoldenPath, string OutPath)
{
    public static Args Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        return new Args(
            Get("api", "http://localhost:5080"),
            Get("email", "demo@ragassistant.local"),
            Get("password", "demo-password-123"),
            int.Parse(Get("k", "6"), CultureInfo.InvariantCulture),
            Get("golden", "eval/golden-set.json"),
            Get("out", "eval/results/latest.md"));
    }
}

internal static class Report
{
    public static string Build(List<ItemResult> results, Args options, JsonElement info, bool judged)
    {
        var answerable = results.Where(r => r.Item.Answerable).ToList();
        var unanswerable = results.Where(r => !r.Item.Answerable).ToList();
        var faith = answerable.Where(r => r.Faithfulness is not null).Select(r => r.Faithfulness!.Value).ToList();
        string Pct(int n, int d) => d == 0 ? "n/a" : $"{100.0 * n / d:0}% ({n}/{d})";

        var chat = info.GetProperty("chat");
        var emb = info.GetProperty("embeddings");
        var sb = new StringBuilder();
        sb.AppendLine($"Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · chat model: `{chat.GetProperty("provider")}/{chat.GetProperty("model")}` · embeddings: `{emb.GetProperty("provider")}/{emb.GetProperty("model")}` · k = {options.K}");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Retrieval hit-rate@{options.K} (gold page in top-{options.K} chunks) | {Pct(answerable.Count(r => r.HitRank is not null), answerable.Count)} |");
        var mrr = answerable.Count == 0 ? 0 : answerable.Sum(r => r.HitRank is { } rank ? 1.0 / rank : 0) / answerable.Count;
        sb.AppendLine($"| Mean reciprocal rank of the gold chunk | {mrr:0.00} |");
        sb.AppendLine($"| Answered (not \"not found\") on answerable questions | {Pct(answerable.Count(r => r.AnswerFound), answerable.Count)} |");
        sb.AppendLine($"| Citation correctness (cites gold document, page ±1) | {Pct(answerable.Count(r => r.CitationCorrect), answerable.Count)} |");
        sb.AppendLine($"| Correct \"not found\" on unanswerable questions | {Pct(unanswerable.Count(r => !r.AnswerFound), unanswerable.Count)} |");
        sb.AppendLine(judged
            ? $"| Faithfulness (LLM-as-judge, 0–1, {faith.Count} judged answers) | {(faith.Count == 0 ? "n/a" : faith.Average().ToString("0.00", CultureInfo.InvariantCulture))} |"
            : "| Faithfulness (LLM-as-judge) | not run: no LLM API key configured |");
        return sb.ToString();
    }
}
