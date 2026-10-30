using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RagAssistant.Application.Abstractions;

namespace RagAssistant.Api.Infrastructure;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid UserId => GetUserId(accessor.HttpContext?.User)
        ?? throw new UnauthorizedAccessException("No authenticated user.");

    public static Guid? GetUserId(ClaimsPrincipal? principal)
    {
        var sub = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
