using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
using RagAssistant.Application.Chat;
using SkChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace RagAssistant.Infrastructure.Ai;

/// <summary>
/// Offline stand-in for an LLM, used in demo mode and integration tests. It is extractive, not generative:
/// it returns the source sentences with the highest word overlap with the question, with [docN:page] citations,
/// and streams them word by word. Good enough to demo the full pipeline (retrieval, streaming, citations,
/// "not found"); not a measure of answer quality.
/// </summary>
public sealed partial class MockChatCompletionService(TimeSpan? tokenDelay = null) : IChatCompletionService
{
    public const string ModelName = "mock-extractive-v1";
    private readonly TimeSpan _delay = tokenDelay ?? TimeSpan.FromMilliseconds(12);

    private static readonly HashSet<string> StopWords =
    [
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "were", "be", "by", "with",
        "as", "at", "it", "its", "this", "that", "what", "which", "who", "how", "why", "when", "does", "do", "did",
        "can", "should", "about", "from", "into", "their", "there", "they", "we", "you", "your", "my", "me", "tell",
        "explain", "describe", "according", "document", "documents", "please"
    ];

    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?> { [AIServiceExtensions.ModelIdKey] = ModelName };

    public async Task<IReadOnlyList<SkChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        return [new SkChatMessageContent(AuthorRole.Assistant, Respond(chatHistory), ModelName)];
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null, Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (Match piece in StreamPieces().Matches(Respond(chatHistory)))
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cancellationToken);
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, piece.Value, modelId: ModelName);
        }
    }

    private static string Respond(ChatHistory history)
    {
        var system = history.FirstOrDefault(m => m.Role == AuthorRole.System)?.Content ?? "";
        var last = history.LastOrDefault(m => m.Role == AuthorRole.User)?.Content ?? "";

        if (system.StartsWith(PromptBuilder.RewriteTaskMarker)) return Rewrite(last);
        if (system.StartsWith(PromptBuilder.JudgeTaskMarker)) return "{\"score\": null, \"reason\": \"mock provider cannot judge\"}";
        return Answer(last);
    }

    private static string Rewrite(string content)
    {
        var question = content[(content.LastIndexOf("Last user message:", StringComparison.Ordinal) + 18)..].Trim();
        if (Words(question).Count > 6) return question;
        var previous = ConversationUser().Matches(content).Select(m => m.Groups[1].Value.Trim()).LastOrDefault();
        return previous is null ? question : $"{previous} {question}";
    }

    private static string Answer(string content)
    {
        var questionMatch = QuestionBlock().Match(content);
        var question = questionMatch.Success ? questionMatch.Groups[1].Value : content;
        var terms = Words(question).ToHashSet();
        if (terms.Count == 0) return PromptBuilder.NotFoundAnswer;

        var candidates = new List<(double Score, string Sentence, string Citation, int Order)>();
        var order = 0;
        foreach (Match source in SourceBlock().Matches(content))
        {
            var alias = source.Groups["id"].Value;
            var page = source.Groups["pages"].Value.Split('-')[0];
            // The text alternates between <page n="X"/> markers and that page's text.
            var parts = PageMarker().Split(source.Groups["text"].Value);
            var segments = new List<(string Page, string Text)> { (page, parts[0]) };
            for (var i = 1; i + 1 < parts.Length; i += 2) segments.Add((parts[i], parts[i + 1]));

            foreach (var (segmentPage, sentence) in segments.SelectMany(seg => Sentences().Split(seg.Text).Select(s => (seg.Page, s))))
            {
                var s = sentence.Trim();
                if (s.Length < 60 || s.Length > 600) continue; // skip headings and run-on fragments
                var words = Words(s);
                var overlap = terms.Count(words.Contains);
                if (overlap == 0) continue;
                // Favour sentences covering more of the question; mild length normalisation.
                var score = overlap / (double)terms.Count + overlap / Math.Sqrt(words.Count + 4) * 0.3;
                candidates.Add((score, s, $"[{alias}:{segmentPage}]", order++));
            }
        }

        var minOverlap = Math.Min(2, terms.Count);
        var best = candidates
            .Where(c => terms.Count(Words(c.Sentence).Contains) >= minOverlap)
            .OrderByDescending(c => c.Score).ThenBy(c => c.Order)
            .DistinctBy(c => c.Sentence)
            .Take(2)
            .ToList();
        if (best.Count == 0) return PromptBuilder.NotFoundAnswer;

        var sb = new StringBuilder("Here is what your documents say (demo mode: extractive mock model):\n\n");
        foreach (var c in best) sb.Append("- ").Append(WebUtility.HtmlDecode(c.Sentence)).Append(' ').Append(c.Citation).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static List<string> Words(string text) =>
        WordRegex().Matches(text.ToLowerInvariant()).Select(m => Stem(m.Value))
            .Where(w => w.Length > 2 && !StopWords.Contains(w)).ToList();

    private static string Stem(string w) =>
        w.Length > 4 && w.EndsWith("ies") ? w[..^3] + "y"
        : w.Length > 4 && w.EndsWith("ing") ? w[..^3]
        : w.Length > 3 && w.EndsWith("ed") ? w[..^2]
        : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1]
        : w;

    [GeneratedRegex(@"<source id=""(?<id>doc\d+)""[^>]*pages=""(?<pages>[\d-]+)"">\s*(?<text>.*?)\s*</source>", RegexOptions.Singleline)]
    private static partial Regex SourceBlock();

    [GeneratedRegex(@"<page n=""(\d+)""/>")]
    private static partial Regex PageMarker();

    [GeneratedRegex(@"<question>\s*(.*?)\s*</question>", RegexOptions.Singleline)]
    private static partial Regex QuestionBlock();

    [GeneratedRegex(@"^User: (.*)$", RegexOptions.Multiline)]
    private static partial Regex ConversationUser();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentences();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\S+\s*")]
    private static partial Regex StreamPieces();
}
