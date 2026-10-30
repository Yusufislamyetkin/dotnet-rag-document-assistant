using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using RagAssistant.Api.Infrastructure;
using RagAssistant.Infrastructure.Auth;
using RagAssistant.Infrastructure.Identity;

namespace RagAssistant.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record AuthRequest(string Email, string Password);
    public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, string Email);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (AuthRequest req, UserManager<AppUser> users, JwtTokenService tokens) =>
        {
            if (!new EmailAddressAttribute().IsValid(req.Email))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Invalid email address."] });

            var user = new AppUser { Email = req.Email.Trim(), UserName = req.Email.Trim() };
            var result = await users.CreateAsync(user, req.Password ?? "");
            if (!result.Succeeded)
                return Results.ValidationProblem(result.Errors.GroupBy(e => e.Code)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));

            var token = tokens.Create(user);
            return Results.Ok(new AuthResponse(token.Token, token.ExpiresAt, user.Email));
        }).RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/login", async (AuthRequest req, UserManager<AppUser> users, JwtTokenService tokens) =>
        {
            var user = await users.FindByEmailAsync((req.Email ?? "").Trim());
            if (user is null || await users.IsLockedOutAsync(user))
                return Results.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);

            if (!await users.CheckPasswordAsync(user, req.Password ?? ""))
            {
                await users.AccessFailedAsync(user);
                return Results.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);
            }

            await users.ResetAccessFailedCountAsync(user);
            var token = tokens.Create(user);
            return Results.Ok(new AuthResponse(token.Token, token.ExpiresAt, user.Email!));
        }).RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapGet("/me", (HttpContext ctx) =>
            Results.Ok(new { userId = HttpCurrentUser.GetUserId(ctx.User), email = ctx.User.FindFirst("email")?.Value }))
            .RequireAuthorization();
    }
}
