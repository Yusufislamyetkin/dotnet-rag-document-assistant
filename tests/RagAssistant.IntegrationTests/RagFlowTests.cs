using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RagAssistant.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class RagFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly byte[] HandbookPdf = TestPdf.Create(
        "Northwind Logistics employee handbook. Employees receive twenty five days of paid annual leave per calendar year. " +
        "Leave requests must be submitted through the HR portal at least two weeks in advance.",
        "The Zephyr warehouse is located in Rotterdam and stores spare turbine blades for offshore wind farms. " +
        "Access to the Zephyr warehouse requires a yellow security badge issued by facility management.",
        "Expense reports must be filed within thirty days. Receipts above fifty euros must be attached to every expense report.");

    [Fact]
    public async Task Health_endpoint_reports_database()
    {
        var res = await factory.CreateClient().GetStringAsync("/health");
        Assert.Contains("\"postgres\":\"Healthy\"", res);
    }

    [Fact]
    public async Task Endpoints_require_authentication()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/chat/stream", new { question = "hi" })).StatusCode);
    }

    [Fact]
    public async Task Upload_validates_and_deduplicates_by_content_hash()
    {
        var client = await factory.CreateUserClientAsync();

        var first = await Upload(client, HandbookPdf, "handbook.pdf");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var duplicate = await Upload(client, HandbookPdf, "copy-of-handbook.pdf");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("Duplicate", await duplicate.Content.ReadAsStringAsync());

        var notPdf = await Upload(client, Encoding.UTF8.GetBytes("just text"), "fake.pdf");
        Assert.Equal(HttpStatusCode.BadRequest, notPdf.StatusCode);
        Assert.Contains("not a valid PDF", await notPdf.Content.ReadAsStringAsync());

        var wrongType = await Upload(client, HandbookPdf, "handbook.docx");
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
    }

    [Fact]
    public async Task Uploaded_pdf_is_ingested_and_answers_stream_with_page_citations()
    {
        var client = await factory.CreateUserClientAsync();
        var docId = await UploadAndWaitReady(client, HandbookPdf, "handbook.pdf");

        var events = await Chat(client, new { question = "Where is the Zephyr warehouse located and what does it store?" });

        var meta = events.First(e => e.Type == "meta");
        Assert.Equal("meta", events[0].Type);
        Assert.True(events.Count(e => e.Type == "token") > 5, "answer should be streamed in several token events");
        var answer = string.Concat(events.Where(e => e.Type == "token").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Contains("Rotterdam", answer);

        var citations = events.Single(e => e.Type == "citations").Data.GetProperty("citations").EnumerateArray().ToList();
        Assert.NotEmpty(citations);
        Assert.All(citations, c => Assert.Equal(docId, c.GetProperty("documentId").GetGuid()));
        Assert.Contains(citations, c => c.GetProperty("page").GetInt32() == 2);

        var done = events.Single(e => e.Type == "done").Data;
        Assert.True(done.GetProperty("answerFound").GetBoolean());
        Assert.True(done.GetProperty("inputTokens").GetInt32() > 0);
        Assert.Equal("done", events[^1].Type);

        // History is persisted with structured citations.
        var sessionId = meta.Data.GetProperty("sessionId").GetGuid();
        var messages = await client.GetFromJsonAsync<JsonElement>($"/api/chat/sessions/{sessionId}/messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.NotEmpty(messages[1].GetProperty("citations").EnumerateArray());
    }

    [Fact]
    public async Task Unrelated_question_gets_not_found_answer_without_llm_call()
    {
        var client = await factory.CreateUserClientAsync();
        await UploadAndWaitReady(client, HandbookPdf, "handbook.pdf");

        var events = await Chat(client, new { question = "Who painted the Mona Lisa?" });

        var answer = string.Concat(events.Where(e => e.Type == "token").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Equal("I couldn't find the answer to that in your documents.", answer);
        var done = events.Single(e => e.Type == "done").Data;
        Assert.False(done.GetProperty("answerFound").GetBoolean());
        Assert.Equal(0, done.GetProperty("inputTokens").GetInt32());
        Assert.Empty(events.Single(e => e.Type == "citations").Data.GetProperty("citations").EnumerateArray());
    }

    [Fact]
    public async Task Follow_up_question_is_rewritten_into_a_standalone_query()
    {
        var client = await factory.CreateUserClientAsync();
        await UploadAndWaitReady(client, HandbookPdf, "handbook.pdf");

        var first = await Chat(client, new { question = "How many days of paid annual leave do employees get?" });
        var sessionId = first[0].Data.GetProperty("sessionId").GetGuid();

        var second = await Chat(client, new { sessionId, question = "How do I request it?" });
        var searchQuery = second[0].Data.GetProperty("searchQuery").GetString()!;

        Assert.NotEqual("How do I request it?", searchQuery);
        Assert.Contains("annual leave", searchQuery);
        Assert.Equal(sessionId, second[0].Data.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task Users_only_see_and_search_their_own_documents()
    {
        var owner = await factory.CreateUserClientAsync();
        var docId = await UploadAndWaitReady(owner, HandbookPdf, "handbook.pdf");
        var other = await factory.CreateUserClientAsync();

        Assert.Empty(await other.GetFromJsonAsync<JsonElement[]>("/api/documents") ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/documents/{docId}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/documents/{docId}")).StatusCode);

        var search = await other.PostAsJsonAsync("/api/search", new { query = "Zephyr warehouse Rotterdam" });
        var body = await search.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("chunks").EnumerateArray());

        var filtered = await other.PostAsJsonAsync("/api/chat/stream", new { question = "Zephyr?", documentIds = new[] { docId } });
        Assert.Equal(HttpStatusCode.BadRequest, filtered.StatusCode);

        // The owner can download the original file.
        var file = await owner.GetAsync($"/api/documents/{docId}/file");
        Assert.Equal("application/pdf", file.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Document_filter_limits_retrieval_to_selected_documents()
    {
        var client = await factory.CreateUserClientAsync();
        var handbook = await UploadAndWaitReady(client, HandbookPdf, "handbook.pdf");
        var other = await UploadAndWaitReady(client,
            TestPdf.Create("The cafeteria serves vegetarian lasagna every Wednesday and fresh fruit every morning."), "cafeteria.pdf");

        var res = await client.PostAsJsonAsync("/api/search", new { query = "Zephyr warehouse badge", documentIds = new[] { other } });
        var chunks = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chunks").EnumerateArray().ToList();

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal(other, c.GetProperty("documentId").GetGuid()));
        Assert.DoesNotContain(chunks, c => c.GetProperty("documentId").GetGuid() == handbook);
    }

    [Fact]
    public async Task Question_length_is_limited()
    {
        var client = await factory.CreateUserClientAsync();
        var res = await client.PostAsJsonAsync("/api/chat/stream", new { question = new string('a', 2001) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Usage_is_logged_per_operation()
    {
        var client = await factory.CreateUserClientAsync();
        await UploadAndWaitReady(client, HandbookPdf, "handbook.pdf");
        await Chat(client, new { question = "What must be attached to an expense report?" });

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/usage?days=1");
        var operations = usage.GetProperty("byOperation").EnumerateArray().Select(o => o.GetProperty("operation").GetString()).ToList();

        Assert.Contains("embed", operations);
        Assert.Contains("chat", operations);
        Assert.True(usage.GetProperty("totals").GetProperty("inputTokens").GetInt64() > 0);
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> Upload(HttpClient client, byte[] content, string fileName)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "files", fileName);
        return client.PostAsync("/api/documents", form);
    }

    private static async Task<Guid> UploadAndWaitReady(HttpClient client, byte[] content, string fileName)
    {
        var res = await Upload(client, content, fileName);
        var results = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = results[0].GetProperty("document").GetProperty("id").GetGuid();

        for (var i = 0; i < 100; i++)
        {
            var doc = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{id}");
            var status = doc.GetProperty("status").GetString();
            if (status == "Ready") return id;
            if (status == "Failed") throw new Xunit.Sdk.XunitException("Ingestion failed: " + doc.GetProperty("errorMessage"));
            await Task.Delay(100);
        }
        throw new TimeoutException("Document was not ingested in time.");
    }

    private sealed record SseEvent(string Type, JsonElement Data);

    private static async Task<List<SseEvent>> Chat(HttpClient client, object request)
    {
        var res = await client.PostAsJsonAsync("/api/chat/stream", request);
        res.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);

        var events = new List<SseEvent>();
        foreach (var block in (await res.Content.ReadAsStringAsync()).Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var type = lines.FirstOrDefault(l => l.StartsWith("event:"))?[6..].Trim();
            var data = lines.FirstOrDefault(l => l.StartsWith("data:"))?[5..].Trim();
            if (type is not null && data is not null) events.Add(new SseEvent(type, JsonDocument.Parse(data).RootElement.Clone()));
        }
        return events;
    }
}
