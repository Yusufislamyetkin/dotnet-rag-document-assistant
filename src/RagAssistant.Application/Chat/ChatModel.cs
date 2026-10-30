namespace RagAssistant.Application.Chat;

public enum PromptRole { User, Assistant }

public sealed record PromptMessage(PromptRole Role, string Content);

public sealed record ChatPrompt(string System, IReadOnlyList<PromptMessage> Messages);

public sealed record TokenUsage(int InputTokens, int OutputTokens);

/// <summary>A streamed piece of the answer, or (last) the token usage of the call.</summary>
public sealed record ChatStreamUpdate(string? Text, TokenUsage? Usage = null);

public sealed record ChatCompletion(string Text, TokenUsage Usage);

/// <summary>Provider-neutral chat model. Implementations wrap a Semantic Kernel IChatCompletionService.</summary>
public interface IChatModel
{
    string Provider { get; }
    string Model { get; }
    IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatPrompt prompt, int maxTokens, CancellationToken ct);
    Task<ChatCompletion> CompleteAsync(ChatPrompt prompt, int maxTokens, CancellationToken ct);
}

/// <summary>Model used for cheap auxiliary calls (query rewriting, eval judging). May be the same as the answer model.</summary>
public interface IAuxiliaryChatModel : IChatModel;
