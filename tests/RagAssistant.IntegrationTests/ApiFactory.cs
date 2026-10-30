using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RagAssistant.IntegrationTests;

/// <summary>
/// Boots the real API against PostgreSQL + pgvector in a container, with the offline mock LLM and hashing embeddings,
/// so the suite needs Docker but no API keys.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "rag-tests-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync() => await _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _db.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-0123456789");
        builder.UseSetting("Llm:Provider", "Mock");
        builder.UseSetting("Embeddings:Provider", "Mock");
        builder.UseSetting("Demo:SeedSamples", "false");
        builder.UseSetting("Storage:Root", _storage);
        builder.UseSetting("RateLimiting:ChatPerMinute", "1000");
        builder.UseSetting("RateLimiting:UploadPerMinute", "1000");
        builder.UseSetting("RateLimiting:AuthPerMinute", "1000");
    }

    public async Task<HttpClient> CreateUserClientAsync(string? email = null)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register",
            new { email = email ?? $"user-{Guid.NewGuid():N}@test.local", password = "integration-pass-1" });
        res.EnsureSuccessStatusCode();
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, string Email);
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

public static class TestPdf
{
    /// <summary>Builds a small multi-page PDF; each string is the text of one page.</summary>
    public static byte[] Create(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 780.0;
            foreach (var line in Wrap(text, 85))
            {
                page.AddText(line, 11, new PdfPoint(50, y), font);
                y -= 16;
            }
        }
        return builder.Build();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + word.Length + 1 > width)
            {
                yield return line;
                line = "";
            }
            line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) yield return line;
    }
}
