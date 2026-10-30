using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using RagAssistant.Application;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Documents;
using RagAssistant.Application.Ingestion;
using RagAssistant.Application.Usage;
using RagAssistant.Infrastructure.Ai;
using RagAssistant.Infrastructure.Auth;
using RagAssistant.Infrastructure.Demo;
using RagAssistant.Infrastructure.Identity;
using RagAssistant.Infrastructure.Ingestion;
using RagAssistant.Infrastructure.Persistence;

namespace RagAssistant.Infrastructure;

public static class DependencyInjection
{
    public static void ConfigureDb(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, o => o.UseVector()).UseSnakeCaseNamingConvention();

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<AppDbContext>(o => ConfigureDb(o, connectionString));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton(TimeProvider.System);

        AddIdentity(services, config);
        AddIngestion(services, config);
        AddAi(services, config);
        return services;
    }

    private static void AddIdentity(IServiceCollection services, IConfiguration config)
    {
        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section))
            .Validate(o => o.SigningKey.Length >= 32, "Jwt:SigningKey must be at least 32 characters.")
            .ValidateOnStart();
        services.AddSingleton<JwtTokenService>();
    }

    private static void AddIngestion(IServiceCollection services, IConfiguration config)
    {
        services.Configure<IngestionOptions>(config.GetSection(IngestionOptions.Section));
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.Configure<PricingOptions>(config.GetSection(PricingOptions.Section));

        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton<TextChunker>();
        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<ChannelIngestionQueue>();
        services.AddSingleton<IIngestionQueue>(sp => sp.GetRequiredService<ChannelIngestionQueue>());
        services.AddHostedService<IngestionWorker>();
        services.Configure<DemoOptions>(config.GetSection(DemoOptions.Section));
        services.AddHostedService<DemoSeeder>();

        services.AddScoped<UsageRecorder>();
        services.AddScoped<DocumentService>();
        services.AddScoped<IngestionProcessor>();
    }

    private static void AddAi(IServiceCollection services, IConfiguration config)
    {
        services.Configure<LlmOptions>(config.GetSection(LlmOptions.Section));
        services.Configure<EmbeddingOptions>(config.GetSection(EmbeddingOptions.Section));

        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            var tokens = sp.GetRequiredService<ITokenCounter>();
            var logger = sp.GetRequiredService<ILogger<EmbeddingService>>();

            if (o.Resolve() == EmbeddingProviderKind.OpenAI)
            {
                var kernel = Kernel.CreateBuilder()
                    .AddOpenAIEmbeddingGenerator(o.OpenAI.Model, o.OpenAI.ApiKey!, dimensions: AppDbContext.EmbeddingDimensions)
                    .Build();
                var generator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
                return new EmbeddingService(generator, "OpenAI", o.OpenAI.Model, tokens, logger);
            }

            return new EmbeddingService(new HashingEmbeddingGenerator(AppDbContext.EmbeddingDimensions), "Mock",
                HashingEmbeddingGenerator.ModelName, tokens, logger);
        });
    }
}
