namespace RagAssistant.Application.Abstractions;

public interface ITokenCounter
{
    int Count(string text);
}

public sealed record PageText(int PageNumber, string Text);

public interface IPdfTextExtractor
{
    /// <summary>Extracts raw text per page (1-based page numbers).</summary>
    IReadOnlyList<PageText> Extract(Stream pdf);
}

public interface IFileStorage
{
    Task<string> SaveAsync(Guid userId, Guid documentId, Stream content, CancellationToken ct);
    Stream OpenRead(string storagePath);
    void Delete(string storagePath);
}

public interface IIngestionQueue
{
    ValueTask EnqueueAsync(Guid documentId, CancellationToken ct = default);
}

public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, int InputTokens);

public interface IEmbeddingService
{
    string Provider { get; }
    string Model { get; }
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}
