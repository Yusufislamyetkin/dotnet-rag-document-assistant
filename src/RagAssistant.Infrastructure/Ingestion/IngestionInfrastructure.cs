using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Ingestion;
using RagAssistant.Domain;
using RagAssistant.Infrastructure.Persistence;

namespace RagAssistant.Infrastructure.Ingestion;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    public string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "uploads");
}

public sealed class LocalFileStorage(IOptions<StorageOptions> options) : IFileStorage
{
    public async Task<string> SaveAsync(Guid userId, Guid documentId, Stream content, CancellationToken ct)
    {
        var relative = Path.Combine(userId.ToString("N"), documentId.ToString("N") + ".pdf");
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = File.Create(full);
        await content.CopyToAsync(file, ct);
        return relative.Replace('\\', '/');
    }

    public Stream OpenRead(string storagePath) => File.OpenRead(Resolve(storagePath));

    public void Delete(string storagePath)
    {
        var full = Resolve(storagePath);
        if (File.Exists(full)) File.Delete(full);
    }

    private string Resolve(string relative)
    {
        var root = Path.GetFullPath(options.Value.Root);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid storage path.");
        return full;
    }
}

public sealed class TiktokenTokenCounter : ITokenCounter
{
    // cl100k_base is the tokenizer of text-embedding-3-*; for Claude it is an approximation used for chunk sizing.
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : Tokenizer.CountTokens(text);
}

public sealed class ChannelIngestionQueue : IIngestionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask EnqueueAsync(Guid documentId, CancellationToken ct = default) => _channel.Writer.WriteAsync(documentId, ct);
}

/// <summary>Reads document ids from the channel and runs the ingestion pipeline one document at a time.</summary>
public sealed class IngestionWorker(ChannelIngestionQueue queue, IServiceScopeFactory scopes, ILogger<IngestionWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeuePendingAsync(stoppingToken);

        await foreach (var documentId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IngestionProcessor>().ProcessAsync(documentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled ingestion error for {DocumentId}", documentId);
            }
        }
    }

    /// <summary>Documents left in Uploaded/Processing by a restart are picked up again.</summary>
    private async Task RequeuePendingAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = await db.Documents
                .Where(d => d.Status == DocumentStatus.Uploaded || d.Status == DocumentStatus.Processing)
                .OrderBy(d => d.CreatedAt).Select(d => d.Id).ToListAsync(ct);
            foreach (var id in pending) await queue.EnqueueAsync(id, ct);
            if (pending.Count > 0) logger.LogInformation("Re-queued {Count} pending documents", pending.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not re-queue pending documents");
        }
    }
}
