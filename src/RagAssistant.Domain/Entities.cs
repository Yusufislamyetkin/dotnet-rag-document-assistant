namespace RagAssistant.Domain;

public enum DocumentStatus
{
    Uploaded = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3
}

public sealed class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public int PageCount { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;
    public string? ErrorMessage { get; set; }
    public string StoragePath { get; set; } = "";
    /// <summary>Embedding model used for this document's chunks. Retrieval only searches chunks of the active model.</summary>
    public string? EmbeddingModel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }

    public List<DocumentChunk> Chunks { get; set; } = [];

    public void MarkProcessing()
    {
        Status = DocumentStatus.Processing;
        ErrorMessage = null;
    }

    public void MarkReady(int pageCount, string embeddingModel)
    {
        Status = DocumentStatus.Ready;
        PageCount = pageCount;
        EmbeddingModel = embeddingModel;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string error)
    {
        Status = DocumentStatus.Failed;
        ErrorMessage = error.Length > 1000 ? error[..1000] : error;
        ProcessedAt = DateTimeOffset.UtcNow;
    }
}

public sealed class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int ChunkIndex { get; set; }
    public int PageStart { get; set; }
    public int PageEnd { get; set; }
    /// <summary>Character offset of the chunk start inside the cleaned text of <see cref="PageStart"/>.</summary>
    public int CharStart { get; set; }
    /// <summary>Character offset of the chunk end inside the cleaned text of <see cref="PageEnd"/>.</summary>
    public int CharEnd { get; set; }
    public string Content { get; set; } = "";
    public int TokenCount { get; set; }
    public float[]? Embedding { get; set; }

    public Document? Document { get; set; }
}

public sealed class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Title { get; set; } = "New chat";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; set; } = [];
}

public enum ChatRole
{
    User = 0,
    Assistant = 1
}

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    public string? RewrittenQuery { get; set; }
    /// <summary>Serialized citation list (jsonb).</summary>
    public string? CitationsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LlmUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ChatMessageId { get; set; }
    public string Operation { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public int LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class UsageOperations
{
    public const string Chat = "chat";
    public const string Rewrite = "rewrite";
    public const string Embed = "embed";
    public const string Judge = "judge";
}
