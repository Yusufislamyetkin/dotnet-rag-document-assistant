using System.Runtime.CompilerServices;
using System.Text;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
using RagAssistant.Application.Chat;
using SkChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace RagAssistant.Infrastructure.Ai;

/// <summary>
/// Semantic Kernel chat connector for Anthropic Claude, built on the official Anthropic C# SDK.
/// Token usage is attached to the last streamed chunk under <see cref="SemanticKernelChatModel.UsageMetadataKey"/>.
/// </summary>
public sealed class AnthropicChatCompletionService(AnthropicClient client, string model) : IChatCompletionService
{
    public const string RefusalMessage = "The model declined to answer this request.";

    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?> { [AIServiceExtensions.ModelIdKey] = model };

    public async Task<IReadOnlyList<SkChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        object? usage = null;
        await foreach (var chunk in GetStreamingChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken))
        {
            text.Append(chunk.Content);
            if (chunk.Metadata?.TryGetValue(SemanticKernelChatModel.UsageMetadataKey, out var u) == true) usage = u;
        }
        return
        [
            new SkChatMessageContent(AuthorRole.Assistant, text.ToString(), model,
                metadata: new Dictionary<string, object?> { [SemanticKernelChatModel.UsageMetadataKey] = usage })
        ];
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null, Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var maxTokens = executionSettings?.ExtensionData?.TryGetValue("max_tokens", out var mt) == true ? Convert.ToInt32(mt) : 1500;
        var system = string.Join("\n\n", chatHistory.Where(m => m.Role == AuthorRole.System).Select(m => m.Content));
        var messages = chatHistory
            .Where(m => m.Role != AuthorRole.System)
            .Select(m => m.Role == AuthorRole.Assistant
                ? (MessageParam)new AssistantMessageParam { Content = m.Content ?? "" }
                : new UserMessageParam { Content = m.Content ?? "" })
            .ToList();

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxTokens,
            System = system,
            Messages = messages,
            // Short grounded answers: low effort keeps latency and thinking tokens down.
            OutputConfig = new OutputConfig { Effort = Effort.Low }
        };

        long inputTokens = 0, outputTokens = 0;
        var refused = false;
        await foreach (var e in client.Messages.CreateStreaming(parameters, cancellationToken))
        {
            if (e.TryPickStart(out var start))
                inputTokens = start.Message.Usage.InputTokens;
            else if (e.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, text.Text, modelId: model);
            else if (e.TryPickDelta(out var messageDelta))
            {
                outputTokens = messageDelta.Usage.OutputTokens;
                refused = messageDelta.Delta.StopReason?.Raw() == "refusal";
            }
        }

        if (refused)
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, RefusalMessage, modelId: model);

        yield return new StreamingChatMessageContent(AuthorRole.Assistant, null, modelId: model,
            metadata: new Dictionary<string, object?>
            {
                [SemanticKernelChatModel.UsageMetadataKey] = new TokenUsage((int)inputTokens, (int)outputTokens)
            });
    }
}
