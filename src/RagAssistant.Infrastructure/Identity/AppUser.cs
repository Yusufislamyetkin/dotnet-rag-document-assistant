using Microsoft.AspNetCore.Identity;

namespace RagAssistant.Infrastructure.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
