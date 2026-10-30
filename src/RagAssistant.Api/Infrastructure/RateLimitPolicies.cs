using System.Threading.RateLimiting;
using RagAssistant.Api.Infrastructure;

namespace RagAssistant.Api;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Chat = "chat";
    public const string Upload = "upload";

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var chatPerMinute = config.GetValue("RateLimiting:ChatPerMinute", 20);
        var uploadPerMinute = config.GetValue("RateLimiting:UploadPerMinute", 10);
        var authPerMinute = config.GetValue("RateLimiting:AuthPerMinute", 10);

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                await ctx.HttpContext.Response.WriteAsJsonAsync(
                    new { title = "Too many requests", status = 429, detail = "Rate limit exceeded. Try again later." }, ct);
            };

            // Authenticated endpoints are partitioned per user id, anonymous ones per IP.
            o.AddPolicy(Chat, ctx => FixedWindow(PartitionKey(ctx), chatPerMinute));
            o.AddPolicy(Upload, ctx => FixedWindow(PartitionKey(ctx), uploadPerMinute));
            o.AddPolicy(Auth, ctx => FixedWindow("ip:" + ctx.Connection.RemoteIpAddress, authPerMinute));
        });
        return services;
    }

    private static string PartitionKey(HttpContext ctx) =>
        HttpCurrentUser.GetUserId(ctx.User) is { } id ? "user:" + id : "ip:" + ctx.Connection.RemoteIpAddress;

    private static RateLimitPartition<string> FixedWindow(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
}
