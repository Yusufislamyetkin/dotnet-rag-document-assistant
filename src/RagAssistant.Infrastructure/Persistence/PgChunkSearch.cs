using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RagAssistant.Application.Retrieval;

namespace RagAssistant.Infrastructure.Persistence;

/// <summary>
/// Raw SQL search over pgvector and PostgreSQL full-text search. Both queries are scoped to the user's own
/// Ready documents; the vector query also only looks at chunks embedded with the active embedding model.
/// </summary>
public sealed class PgChunkSearch(AppDbContext db) : IChunkSearch
{
    private const string VectorSql = """
        SELECT c.id, 1 - (c.embedding <=> CAST(@q AS vector)) AS score
        FROM document_chunks c
        JOIN documents d ON d.id = c.document_id
        WHERE d.user_id = @u AND d.status = 'Ready' AND d.embedding_model = @m AND c.embedding IS NOT NULL
          AND (@all OR d.id = ANY(@ids))
        ORDER BY c.embedding <=> CAST(@q AS vector)
        LIMIT @n
        """;

    // plainto_tsquery ANDs every term, which is too strict for natural-language questions;
    // swapping '&' for '|' turns it into an OR query and lets ts_rank order by how many terms match.
    private const string FullTextSql = """
        WITH q AS (
            SELECT NULLIF(replace(plainto_tsquery('english', @t)::text, '&', '|'), '') AS text
        )
        SELECT c.id, ts_rank(c.search_vector, to_tsquery('english', q.text)) AS score
        FROM document_chunks c
        JOIN documents d ON d.id = c.document_id, q
        WHERE q.text IS NOT NULL AND d.user_id = @u AND d.status = 'Ready'
          AND (@all OR d.id = ANY(@ids))
          AND c.search_vector @@ to_tsquery('english', q.text)
        ORDER BY score DESC, c.id
        LIMIT @n
        """;

    public Task<IReadOnlyList<ScoredId>> VectorSearchAsync(Guid userId, float[] queryEmbedding, string embeddingModel,
        IReadOnlyCollection<Guid>? documentIds, int limit, CancellationToken ct) =>
        QueryAsync(VectorSql, cmd =>
        {
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = ToVectorLiteral(queryEmbedding) });
            cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Text) { Value = embeddingModel });
        }, userId, documentIds, limit, ct);

    public Task<IReadOnlyList<ScoredId>> FullTextSearchAsync(Guid userId, string query,
        IReadOnlyCollection<Guid>? documentIds, int limit, CancellationToken ct) =>
        QueryAsync(FullTextSql, cmd =>
            cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = query }),
            userId, documentIds, limit, ct);

    private async Task<IReadOnlyList<ScoredId>> QueryAsync(string sql, Action<DbCommand> addParameters, Guid userId,
        IReadOnlyCollection<Guid>? documentIds, int limit, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            addParameters(cmd);
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = userId });
            cmd.Parameters.Add(new NpgsqlParameter("all", NpgsqlDbType.Boolean) { Value = documentIds is null or { Count: 0 } });
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                { Value = documentIds?.ToArray() ?? [] });
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = limit });

            var results = new List<ScoredId>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(new ScoredId(reader.GetGuid(0), Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture)));
            return results;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private static string ToVectorLiteral(float[] v)
    {
        var sb = new StringBuilder(v.Length * 10).Append('[');
        for (var i = 0; i < v.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(v[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }
}
