using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Chat;

namespace RagAssistant.Infrastructure.Ai;

/// <summary>
/// Adapts any Semantic Kernel <see cref="IChatCompletionService"/> (OpenAI connector, our Anthropic connector, or the mock)
/// to the application's provider-neutral <see cref="IChatModel"/>.
/// </summary>
public class SemanticKernelChatModel(IChatCompletionService service, string provider, string model, ITokenCounter tokens)
    : IAuxiliaryChatModel
{
    public const string UsageMetadataKey = "Usage";

    public string Provider => provider;
    public string Model => model;

    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatPrompt prompt, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var history = ToHistory(prompt);
        var output = new StringBuilder();
        TokenUsage? usage = null;

        await foreach (var chunk in service.GetStreamingChatMessageContentsAsync(history, Settings(maxTokens), cancellationToken: ct))
        {
            usage = ReadUsage(chunk.Metadata) ?? usage;
            if (string.IsNullOrEmpty(chunk.Content)) continue;
            output.Append(chunk.Content);
            yield return new ChatStreamUpdate(chunk.Content);
        }

        yield return new ChatStreamUpdate(null, usage ?? Estimate(prompt, output.ToString()));
    }

    public async Task<ChatCompletion> CompleteAsync(ChatPrompt prompt, int maxTokens, CancellationToken ct)
    {
        var result = await service.GetChatMessageContentAsync(ToHistory(prompt), Settings(maxTokens), cancellationToken: ct);
        var text = result.Content ?? "";
        return new ChatCompletion(text, ReadUsage(result.Metadata) ?? Estimate(prompt, text));
    }

    private PromptExecutionSettings Settings(int maxTokens) => provider == "OpenAI"
        ? new OpenAIPromptExecutionSettings { MaxTokens = maxTokens, Temperature = 0.1 }
        : new PromptExecutionSettings { ExtensionData = new Dictionary<string, object> { ["max_tokens"] = maxTokens } };

    private static ChatHistory ToHistory(ChatPrompt prompt)
    {
        var history = new ChatHistory(prompt.System);
        foreach (var m in prompt.Messages)
        {
            if (m.Role == PromptRole.User) history.AddUserMessage(m.Content);
            else history.AddAssistantMessage(m.Content);
        }
        return history;
    }

    private static TokenUsage? ReadUsage(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(UsageMetadataKey, out var value) || value is null) return null;
        return value switch
        {
            TokenUsage u => u,
            OpenAI.Chat.ChatTokenUsage u => new TokenUsage(u.InputTokenCount, u.OutputTokenCount),
            _ => null
        };
    }

    /// <summary>Fallback when a provider does not report usage: count with the local tokenizer (approximate).</summary>
    private TokenUsage Estimate(ChatPrompt prompt, string output) =>
        new(tokens.Count(prompt.System) + prompt.Messages.Sum(m => tokens.Count(m.Content)), tokens.Count(output));
}
