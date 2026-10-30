using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain;

namespace RagAssistant.Application.Retrieval;

public sealed record ScoredId(Guid Id, double Score);

public static class ReciprocalRankFusion
{
    /// <summary>
    /// Merges several ranked lists: score(d) = sum over lists of 1 / (k + rank(d)), rank starting at 1.
    /// Only ranks are used, so cosine similarities and ts_rank values never need to be put on a common scale.
    /// </summary>
    public static IReadOnlyList<ScoredId> Fuse(IEnumerable<IReadOnlyList<Guid>> rankings, int k = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        var scores = new Dictionary<Guid, double>();
        var firstSeen = new Dictionary<Guid, int>();
        var order = 0;
        foreach (var ranking in rankings)
        {
            var seen = new HashSet<Guid>();
            for (var i = 0; i < ranking.Count; i++)
            {
                var id = ranking[i];
                if (!seen.Add(id)) continue; // a duplicate inside one list only counts at its best rank
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (k + i + 1);
                firstSeen.TryAdd(id, order++);
            }
        }
        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => firstSeen[kv.Key])
            .Select(kv => new ScoredId(kv.Key, kv.Value))
            .ToList();
    }
}

public interface IChunkSearch
{
    /// <summary>Cosine similarity search (1 - cosine distance), best first.</summary>
    Task<IReadOnlyList<ScoredId>> VectorSearchAsync(Guid userId, float[] queryEmbedding, string embeddingModel,
        IReadOnlyCollection<Guid>? documentIds, int limit, CancellationToken ct);

    /// <summary>PostgreSQL full-text search ranked by ts_rank, best first.</summary>
    Task<IReadOnlyList<ScoredId>> FullTextSearchAsync(Guid userId, string query,
        IReadOnlyCollection<Guid>? documentIds, int limit, CancellationToken ct);
}

public sealed record RetrievedChunk(
    Guid ChunkId, Guid DocumentId, string FileName, int PageStart, int PageEnd, string Content,
    double FusedScore, double? VectorSimilarity, int? VectorRank, int? FullTextRank,
    IReadOnlyList<int>? PageNumbers = null, IReadOnlyList<int>? PageOffsets = null)
{
    public IEnumerable<(int Page, string Text)> Segments() =>
        DocumentChunk.Segments(Content, PageStart, PageNumbers ?? [], PageOffsets ?? []);
}

public sealed record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks, double BestVectorSimilarity, int FullTextHits,
    int EmbeddingTokens)
{
    public bool IsEmpty => Chunks.Count == 0;
}

public sealed class HybridRetriever(
    IAppDbContext db,
    IEmbeddingService embeddings,
    IChunkSearch search,
    IOptions<RetrievalOptions> options)
{
    public async Task<RetrievalResult> RetrieveAsync(Guid userId, string query, IReadOnlyCollection<Guid>? documentIds,
        int? topK, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("retrieval.hybrid");
        var o = options.Value;
        var k = Math.Clamp(topK ?? o.TopK, 1, 50);
        var pool = Math.Max(o.CandidatePoolSize, k);
        var filter = documentIds is { Count: > 0 } ? documentIds : null;

        var embedded = await embeddings.EmbedAsync([query], ct);
        var vector = await search.VectorSearchAsync(userId, embedded.Vectors[0], embeddings.Model, filter, pool, ct);
        var fullText = await search.FullTextSearchAsync(userId, query, filter, pool, ct);

        var fused = ReciprocalRankFusion.Fuse([vector.Select(v => v.Id).ToList(), fullText.Select(f => f.Id).ToList()], o.RrfK)
            .Take(k).ToList();

        var ids = fused.Select(f => f.Id).ToList();
        var rows = await db.DocumentChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.DocumentId, c.Document!.FileName, c.PageStart, c.PageEnd, c.Content, c.PageNumbers, c.PageOffsets })
            .ToDictionaryAsync(c => c.Id, ct);

        var vectorRank = vector.Select((v, i) => (v, i)).ToDictionary(x => x.v.Id, x => (x.i + 1, x.v.Score));
        var textRank = fullText.Select((f, i) => (f, i)).ToDictionary(x => x.f.Id, x => x.i + 1);

        var chunks = fused.Where(f => rows.ContainsKey(f.Id)).Select(f =>
        {
            var r = rows[f.Id];
            var hasVector = vectorRank.TryGetValue(f.Id, out var vr);
            return new RetrievedChunk(r.Id, r.DocumentId, r.FileName, r.PageStart, r.PageEnd, r.Content, f.Score,
                hasVector ? vr.Score : null, hasVector ? vr.Item1 : null,
                textRank.TryGetValue(f.Id, out var tr) ? tr : null, r.PageNumbers, r.PageOffsets);
        }).ToList();

        var best = vector.Count > 0 ? vector[0].Score : 0;
        activity?.SetTag("retrieval.vector_hits", vector.Count);
        activity?.SetTag("retrieval.fulltext_hits", fullText.Count);
        activity?.SetTag("retrieval.best_similarity", best);
        return new RetrievalResult(chunks, best, fullText.Count, embedded.InputTokens);
    }

    /// <summary>
    /// Grounding gate for the "I don't know" path. Only the semantic score is used: with OR-semantics full-text search
    /// almost any question matches some common word, so keyword hits alone are not evidence of relevance.
    /// </summary>
    public bool IsGrounded(RetrievalResult result) =>
        !result.IsEmpty && result.BestVectorSimilarity >= options.Value.MinVectorSimilarity;
}
