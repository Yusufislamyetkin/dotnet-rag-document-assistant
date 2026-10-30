using System.ClientModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using RagAssistant.Application.Abstractions;

namespace RagAssistant.Infrastructure.Ai;

/// <summary>
/// Wraps any <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (OpenAI through Semantic Kernel, or the offline mock)
/// with retry/backoff and token accounting.
/// </summary>
public sealed class EmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    string provider,
    string model,
    ITokenCounter tokens,
    ILogger<EmbeddingService> logger) : IEmbeddingService
{
    private readonly ResiliencePipeline _retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 4,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(1),
            ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
                .Handle<ClientResultException>(e => e.Status is 0 or 408 or 429 or >= 500)
                .Handle<TimeoutException>(),
            OnRetry = args =>
            {
                logger.LogWarning(args.Outcome.Exception, "Embedding call failed, retry {Attempt} in {Delay}",
                    args.AttemptNumber + 1, args.RetryDelay);
                return ValueTask.CompletedTask;
            }
        })
        .Build();

    public string Provider => provider;
    public string Model => model;

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0) return new EmbeddingResult([], 0);
        var result = await _retry.ExecuteAsync(async token => await generator.GenerateAsync(inputs, cancellationToken: token), ct);
        var vectors = result.Select(e => e.Vector.ToArray()).ToList();
        var usedTokens = result.Usage?.InputTokenCount is { } n ? (int)n : inputs.Sum(tokens.Count);
        return new EmbeddingResult(vectors, usedTokens);
    }
}

/// <summary>
/// Offline embedding used in demo mode and tests: feature hashing of word unigrams and bigrams into a fixed-size,
/// L2-normalized vector. It captures lexical overlap only (no semantics), which is enough to exercise the pipeline
/// without an API key. Do not use it to judge retrieval quality of a real deployment.
/// </summary>
public sealed partial class HashingEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ModelName = "mock-hashing-v1";

    private static readonly HashSet<string> StopWords =
    [
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "were", "be", "by", "with",
        "as", "at", "it", "its", "this", "that", "these", "those", "from", "which", "what", "who", "how", "does", "do",
        "can", "should", "may", "will", "not", "no", "into", "about", "than", "then", "their", "there", "they", "we", "you"
    ];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = values.Select(v => new Embedding<float>(Embed(v)) { ModelId = ModelName }).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    public float[] Embed(string text)
    {
        var vector = new float[dimensions];
        var words = Words().Matches(text.ToLowerInvariant()).Select(m => Stem(m.Value))
            .Where(w => w.Length > 1 && !StopWords.Contains(w)).ToList();

        for (var i = 0; i < words.Count; i++)
        {
            Add(vector, words[i], 1f);
            if (i + 1 < words.Count) Add(vector, words[i] + " " + words[i + 1], 0.5f);
        }

        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
        return vector;
    }

    private void Add(float[] vector, string feature, float weight)
    {
        var hash = Fnv1a(feature);
        var index = (int)(hash % (uint)dimensions);
        vector[index] += (hash & 0x8000_0000) == 0 ? weight : -weight;
    }

    // Crude suffix stripping so "risks"/"risk" and "managed"/"manage" collide.
    private static string Stem(string w) =>
        w.Length > 4 && w.EndsWith("ies") ? w[..^3] + "y"
        : w.Length > 4 && w.EndsWith("ing") ? w[..^3]
        : w.Length > 3 && w.EndsWith("ed") ? w[..^2]
        : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1]
        : w;

    private static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var c in s) { hash ^= c; hash *= 16777619u; }
        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
