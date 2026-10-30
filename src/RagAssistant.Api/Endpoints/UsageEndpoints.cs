using Microsoft.Extensions.Options;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Chat;
using RagAssistant.Application.Usage;
using RagAssistant.Infrastructure.Ai;

namespace RagAssistant.Api.Endpoints;

public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/usage", async (int? days, UsageQueries queries, ICurrentUser user, CancellationToken ct) =>
                Results.Ok(await queries.SummaryAsync(user.UserId, days ?? 30, ct)))
            .RequireAuthorization().WithTags("Usage");

        // Which providers are active (no secrets): lets the UI and operators see demo vs real mode.
        app.MapGet("/api/info", (IChatModel chat, IEmbeddingService embeddings) => Results.Ok(new
        {
            chat = new { chat.Provider, chat.Model },
            embeddings = new { embeddings.Provider, embeddings.Model },
            demoMode = chat.Provider == "Mock" || embeddings.Provider == "Mock"
        })).WithTags("Usage");
    }
}
