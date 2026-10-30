using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagAssistant.Application.Documents;
using RagAssistant.Infrastructure.Identity;

namespace RagAssistant.Infrastructure.Demo;

public sealed class DemoOptions
{
    public const string Section = "Demo";
    public bool SeedSamples { get; set; }
    public string Email { get; set; } = "demo@ragassistant.local";
    /// <summary>Public demo password, documented in the README. Only used when <see cref="SeedSamples"/> is true.</summary>
    public string Password { get; set; } = "demo-password-123";
    public string SamplesPath { get; set; } = "samples";
}

/// <summary>Creates a demo user and uploads the PDFs from <c>samples/</c> on first start.</summary>
public sealed class DemoSeeder(IServiceScopeFactory scopes, IOptions<DemoOptions> options, IHostEnvironment env, ILogger<DemoSeeder> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.SeedSamples) return;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByEmailAsync(o.Email);
            if (user is null)
            {
                user = new AppUser { Email = o.Email, UserName = o.Email, EmailConfirmed = true };
                var created = await users.CreateAsync(user, o.Password);
                if (!created.Succeeded)
                {
                    logger.LogWarning("Demo user not created: {Errors}", string.Join("; ", created.Errors.Select(e => e.Description)));
                    return;
                }
            }

            var path = Path.GetFullPath(o.SamplesPath, env.ContentRootPath);
            if (!Directory.Exists(path))
            {
                logger.LogWarning("Demo samples folder {Path} not found", path);
                return;
            }

            var documents = scope.ServiceProvider.GetRequiredService<DocumentService>();
            foreach (var file in Directory.EnumerateFiles(path, "*.pdf").Order())
            {
                await using var stream = File.OpenRead(file);
                var result = await documents.UploadAsync(user.Id, Path.GetFileName(file), stream, stream.Length, ct);
                if (result.Outcome == UploadOutcome.Created)
                    logger.LogInformation("Seeded demo document {File}", Path.GetFileName(file));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Demo seeding failed");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
