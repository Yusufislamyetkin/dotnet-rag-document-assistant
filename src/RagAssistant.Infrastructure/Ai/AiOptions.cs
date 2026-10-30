namespace RagAssistant.Infrastructure.Ai;

public enum LlmProviderKind { Auto, Anthropic, OpenAI, Mock }

public sealed class LlmOptions
{
    public const string Section = "Llm";
    public LlmProviderKind Provider { get; set; } = LlmProviderKind.Auto;
    public AnthropicSettings Anthropic { get; set; } = new();
    public OpenAISettings OpenAI { get; set; } = new();

    public LlmProviderKind Resolve() => Provider switch
    {
        LlmProviderKind.Auto when !string.IsNullOrWhiteSpace(Anthropic.ApiKey) => LlmProviderKind.Anthropic,
        LlmProviderKind.Auto when !string.IsNullOrWhiteSpace(OpenAI.ApiKey) => LlmProviderKind.OpenAI,
        LlmProviderKind.Auto => LlmProviderKind.Mock,
        _ => Provider
    };
}

public sealed class AnthropicSettings
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "claude-sonnet-5-5";
    /// <summary>Optional cheaper model for query rewriting (defaults to <see cref="Model"/>).</summary>
    public string? RewriteModel { get; set; }
}

public sealed class OpenAISettings
{
    public string? ApiKey { get; set; }
    public string ChatModel { get; set; } = "gpt-4.1-mini";
    public string? RewriteModel { get; set; }
}

public enum EmbeddingProviderKind { Auto, OpenAI, Mock }

public sealed class EmbeddingOptions
{
    public const string Section = "Embeddings";
    public EmbeddingProviderKind Provider { get; set; } = EmbeddingProviderKind.Auto;
    public OpenAIEmbeddingSettings OpenAI { get; set; } = new();

    public EmbeddingProviderKind Resolve() => Provider switch
    {
        EmbeddingProviderKind.Auto => string.IsNullOrWhiteSpace(OpenAI.ApiKey) ? EmbeddingProviderKind.Mock : EmbeddingProviderKind.OpenAI,
        _ => Provider
    };
}

public sealed class OpenAIEmbeddingSettings
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "text-embedding-3-small";
}
