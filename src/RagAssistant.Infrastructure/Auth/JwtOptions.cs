namespace RagAssistant.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "rag-document-assistant";
    public string Audience { get; set; } = "rag-document-assistant";
    /// <summary>HMAC signing key, at least 32 characters. Set via env var <c>Jwt__SigningKey</c> or user-secrets.</summary>
    public string SigningKey { get; set; } = "";
    public int ExpiryMinutes { get; set; } = 120;
}
