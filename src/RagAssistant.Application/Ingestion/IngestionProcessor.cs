using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Usage;
using RagAssistant.Domain;

namespace RagAssistant.Application.Ingestion;

/// <summary>extract -> clean -> chunk -> embed (batched) -> save. Runs inside the background ingestion worker.</summary>
public sealed class IngestionProcessor(
    IAppDbContext db,
    IFileStorage storage,
    IPdfTextExtractor extractor,
    TextChunker chunker,
    IEmbeddingService embeddings,
    UsageRecorder usage,
    IOptions<IngestionOptions> options,
    ILogger<IngestionProcessor> logger)
{
    public async Task ProcessAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null || doc.Status == DocumentStatus.Ready) return;

        using var activity = Telemetry.Source.StartActivity("ingest.document");
        activity?.SetTag("document.id", doc.Id);
        doc.MarkProcessing();
        await db.SaveChangesAsync(ct);

        try
        {
            IReadOnlyList<PageText> pages;
            await using (var stream = storage.OpenRead(doc.StoragePath))
                pages = extractor.Extract(stream);

            var cleaned = pages.Select(p => p with { Text = TextCleaner.Clean(p.Text) }).ToList();
            if (cleaned.All(p => p.Text.Length == 0))
                throw new IngestionException("No extractable text found. The PDF is probably scanned images (OCR is on the roadmap).");

            var o = options.Value;
            var drafts = chunker.Chunk(cleaned.Where(p => p.Text.Length > 0).ToList(), o.ChunkTokens, o.ChunkOverlapTokens);
            activity?.SetTag("document.pages", pages.Count);
            activity?.SetTag("document.chunks", drafts.Count);

            // Re-processing after a crash: drop chunks of a previous partial attempt.
            await db.DocumentChunks.Where(c => c.DocumentId == doc.Id).ExecuteDeleteAsync(ct);

            var index = 0;
            foreach (var batch in drafts.Chunk(Math.Max(1, o.EmbeddingBatchSize)))
            {
                var sw = Stopwatch.StartNew();
                var result = await embeddings.EmbedAsync(batch.Select(d => d.Content).ToList(), ct);
                await usage.RecordAsync(doc.UserId, null, UsageOperations.Embed, embeddings.Provider, embeddings.Model,
                    result.InputTokens, 0, (int)sw.ElapsedMilliseconds, ct, save: false);

                for (var i = 0; i < batch.Length; i++)
                {
                    var d = batch[i];
                    db.DocumentChunks.Add(new DocumentChunk
                    {
                        DocumentId = doc.Id,
                        ChunkIndex = index++,
                        PageStart = d.PageStart,
                        CharStart = d.CharStart,
                        PageEnd = d.PageEnd,
                        CharEnd = d.CharEnd,
                        Content = d.Content,
                        TokenCount = d.TokenCount,
                        Embedding = result.Vectors[i]
                    });
                }
            }

            doc.MarkReady(pages.Count, embeddings.Model);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Document {DocumentId} ingested: {Pages} pages, {Chunks} chunks", doc.Id, pages.Count, drafts.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ingestion failed for document {DocumentId}", doc.Id);
            db.ChangeTrackerClear();
            var failed = await db.Documents.FirstAsync(d => d.Id == documentId, CancellationToken.None);
            failed.MarkFailed(ex is IngestionException ? ex.Message : $"Processing failed: {ex.GetType().Name}: {ex.Message}");
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}

public sealed class IngestionException(string message) : Exception(message);
