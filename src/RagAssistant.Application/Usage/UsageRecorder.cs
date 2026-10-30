using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain;

namespace RagAssistant.Application.Usage;

public sealed class ModelPrice
{
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}

/// <summary>USD list prices per million tokens, keyed by model id. Edit in configuration when prices change.</summary>
public sealed class PricingOptions
{
    public const string Section = "Pricing";
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal Estimate(string model, int inputTokens, int outputTokens) =>
        Models.TryGetValue(model, out var p)
            ? (inputTokens * p.InputPerMillion + outputTokens * p.OutputPerMillion) / 1_000_000m
            : 0m;
}

/// <summary>Persists token usage and estimated cost for every LLM / embedding call.</summary>
public sealed class UsageRecorder(IAppDbContext db, IOptions<PricingOptions> pricing, ILogger<UsageRecorder> logger)
{
    private static readonly Counter<long> TokenCounter = Telemetry.Meter.CreateCounter<long>("rag.llm.tokens");
    private static readonly Counter<double> CostCounter = Telemetry.Meter.CreateCounter<double>("rag.llm.cost_usd");

    public async Task<LlmUsage> RecordAsync(Guid userId, Guid? messageId, string operation, string provider, string model,
        int inputTokens, int outputTokens, int latencyMs, CancellationToken ct, bool save = true)
    {
        var cost = pricing.Value.Estimate(model, inputTokens, outputTokens);
        var row = new LlmUsage
        {
            UserId = userId,
            ChatMessageId = messageId,
            Operation = operation,
            Provider = provider,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCostUsd = cost,
            LatencyMs = latencyMs
        };
        db.LlmUsages.Add(row);
        if (save) await db.SaveChangesAsync(ct);

        var tags = new KeyValuePair<string, object?>[] { new("operation", operation), new("model", model) };
        TokenCounter.Add(inputTokens, [.. tags, new("direction", "input")]);
        TokenCounter.Add(outputTokens, [.. tags, new("direction", "output")]);
        CostCounter.Add((double)cost, tags);
        logger.LogInformation("LLM usage {Operation} {Provider}/{Model}: in={InputTokens} out={OutputTokens} cost=${Cost} {LatencyMs}ms",
            operation, provider, model, inputTokens, outputTokens, cost, latencyMs);
        return row;
    }
}
