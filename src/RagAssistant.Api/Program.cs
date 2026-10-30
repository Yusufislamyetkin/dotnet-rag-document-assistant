using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RagAssistant.Api;
using RagAssistant.Api.Endpoints;
using RagAssistant.Api.Infrastructure;
using RagAssistant.Application.Abstractions;
using RagAssistant.Infrastructure;
using RagAssistant.Infrastructure.Auth;
using RagAssistant.Infrastructure.Persistence;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RagAssistant.Application;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var maxUploadBytes = builder.Configuration.GetValue("Ingestion:MaxFileBytes", 20L * 1024 * 1024)
    * builder.Configuration.GetValue("Ingestion:MaxFilesPerUpload", 10) + 1024 * 1024;
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadBytes);

builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAppRateLimiting(builder.Configuration);

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey.PadRight(32))),
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("postgres");

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("rag-document-assistant"))
    .WithTracing(t => t.AddSource(Telemetry.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
    .WithMetrics(m => m.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
// Export only when a collector is configured (standard OTEL_EXPORTER_OTLP_ENDPOINT variable).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    otel.UseOtlpExporter();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = (diag, http) =>
{
    if (HttpCurrentUser.GetUserId(http.User) is { } userId) diag.Set("UserId", userId);
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = (ctx, report) => ctx.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString())
    })
});
app.MapAuthEndpoints();
app.MapDocumentEndpoints();
app.MapChatEndpoints();
app.MapUsageEndpoints();

app.Run();

public partial class Program;
