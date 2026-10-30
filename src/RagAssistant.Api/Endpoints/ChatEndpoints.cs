using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Chat;
using RagAssistant.Application.Retrieval;
using RagAssistant.Domain;

namespace RagAssistant.Api.Endpoints;

public static class ChatEndpoints
{
    public sealed record SearchRequest(string Query, IReadOnlyList<Guid>? DocumentIds, int? TopK);

    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var chat = app.MapGroup("/api/chat").WithTags("Chat").RequireAuthorization();

        // Streams the answer as Server-Sent Events: meta -> token* -> citations -> done (or error).
        chat.MapPost("/stream", async (ChatRequest request, ChatService service, ICurrentUser user,
            ILogger<ChatService> logger, CancellationToken ct) =>
        {
            ChatSession session;
            try
            {
                session = await service.PrepareAsync(user.UserId, request, ct);
            }
            catch (ChatValidationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            return TypedResults.ServerSentEvents(Stream(service, user.UserId, session, request, logger, ct));
        }).RequireRateLimiting(RateLimitPolicies.Chat);

        chat.MapGet("/sessions", async (IAppDbContext db, ICurrentUser user, CancellationToken ct) =>
            Results.Ok(await db.ChatSessions.AsNoTracking()
                .Where(s => s.UserId == user.UserId)
                .OrderByDescending(s => s.UpdatedAt).Take(100)
                .Select(s => new { s.Id, s.Title, s.CreatedAt, s.UpdatedAt })
                .ToListAsync(ct)));

        chat.MapGet("/sessions/{id:guid}/messages", async (Guid id, IAppDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!await db.ChatSessions.AnyAsync(s => s.Id == id && s.UserId == user.UserId, ct)) return Results.NotFound();
            var messages = await db.ChatMessages.AsNoTracking()
                .Where(m => m.SessionId == id).OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Id, m.Role, m.Content, m.RewrittenQuery, m.CitationsJson, m.CreatedAt })
                .ToListAsync(ct);
            return Results.Ok(messages.Select(m => new
            {
                m.Id,
                role = m.Role.ToString().ToLowerInvariant(),
                m.Content,
                m.RewrittenQuery,
                citations = m.CitationsJson is null ? [] : JsonSerializer.Deserialize<List<Citation>>(m.CitationsJson, ChatService.Json),
                m.CreatedAt
            }));
        });

        chat.MapDelete("/sessions/{id:guid}", async (Guid id, IAppDbContext db, ICurrentUser user, CancellationToken ct) =>
            await db.ChatSessions.Where(s => s.Id == id && s.UserId == user.UserId).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent() : Results.NotFound());

        // Retrieval only (no LLM): useful for debugging relevance and used by the eval harness.
        app.MapPost("/api/search", async (SearchRequest request, HybridRetriever retriever, ICurrentUser user, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 2000)
                return Results.Problem("Query must be 1-2000 characters.", statusCode: 400);
            var result = await retriever.RetrieveAsync(user.UserId, request.Query, request.DocumentIds, request.TopK, ct);
            return Results.Ok(new
            {
                grounded = retriever.IsGrounded(result),
                result.BestVectorSimilarity,
                result.FullTextHits,
                chunks = result.Chunks.Select(c => new
                {
                    c.ChunkId, c.DocumentId, c.FileName, c.PageStart, c.PageEnd, c.FusedScore, c.VectorSimilarity,
                    c.VectorRank, c.FullTextRank, preview = c.Content.Length > 300 ? c.Content[..300] + "…" : c.Content
                })
            });
        }).RequireAuthorization().RequireRateLimiting(RateLimitPolicies.Chat).WithTags("Chat");
    }

    private static async IAsyncEnumerable<SseItem<object>> Stream(ChatService service, Guid userId, ChatSession session,
        ChatRequest request, ILogger logger, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var e = service.StreamAsync(userId, session, request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            ChatEvent current;
            string? error = null;
            try
            {
                if (!await e.MoveNextAsync()) break;
                current = e.Current;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break; // client disconnected
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat stream failed for session {SessionId}", session.Id);
                error = "The answer could not be generated. Please try again.";
                current = new ChatErrorEvent(error);
            }

            yield return new SseItem<object>(current, current.Type);
            if (error is not null) yield break;
        }
    }
}
