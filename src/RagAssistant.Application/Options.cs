namespace RagAssistant.Application;

public sealed class IngestionOptions
{
    public const string Section = "Ingestion";
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    public int MaxFilesPerUpload { get; set; } = 10;
    public int ChunkTokens { get; set; } = 800;
    public int ChunkOverlapTokens { get; set; } = 100;
    public int EmbeddingBatchSize { get; set; } = 64;
}

public sealed class RetrievalOptions
{
    public const string Section = "Retrieval";
    /// <summary>Number of fused chunks passed to the LLM.</summary>
    public int TopK { get; set; } = 6;
    /// <summary>Candidates fetched from each retriever (vector and full-text) before fusion.</summary>
    public int CandidatePoolSize { get; set; } = 30;
    /// <summary>Reciprocal Rank Fusion constant (k in 1 / (k + rank)). 60 is the value from the original RRF paper.</summary>
    public int RrfK { get; set; } = 60;
    /// <summary>
    /// If the best vector cosine similarity is below this value and full-text search found nothing,
    /// the assistant answers "not found in your documents" without calling the LLM.
    /// Tune per embedding model: hashed mock embeddings and OpenAI embeddings live on different scales.
    /// </summary>
    public double MinVectorSimilarity { get; set; } = 0.25;
}

public sealed class ChatOptions
{
    public const string Section = "Chat";
    public int MaxQuestionChars { get; set; } = 2000;
    public int HistoryMessages { get; set; } = 6;
    public int MaxAnswerTokens { get; set; } = 1500;
}
