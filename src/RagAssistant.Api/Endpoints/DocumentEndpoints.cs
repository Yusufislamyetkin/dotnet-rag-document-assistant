using Microsoft.Extensions.Options;
using RagAssistant.Application;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Documents;

namespace RagAssistant.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("Documents").RequireAuthorization();

        group.MapPost("/", async (HttpRequest request, DocumentService documents, ICurrentUser user,
            IOptions<IngestionOptions> options, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.Problem("Expected multipart/form-data with one or more 'files'.", statusCode: 400);

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                return Results.Problem("No files were uploaded.", statusCode: 400);
            if (form.Files.Count > options.Value.MaxFilesPerUpload)
                return Results.Problem($"At most {options.Value.MaxFilesPerUpload} files per upload.", statusCode: 400);

            var results = new List<UploadResult>();
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                results.Add(await documents.UploadAsync(user.UserId, file.FileName, stream, file.Length, ct));
            }

            var status = results.Any(r => r.Outcome == UploadOutcome.Created) ? StatusCodes.Status202Accepted
                : results.All(r => r.Outcome == UploadOutcome.Duplicate) ? StatusCodes.Status409Conflict
                : results.Any(r => r.Outcome == UploadOutcome.Duplicate) ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Results.Json(results.Select(r => new
            {
                r.FileName,
                outcome = r.Outcome.ToString(),
                r.Document,
                r.Error
            }), statusCode: status);
        })
        .DisableAntiforgery()
        .RequireRateLimiting(RateLimitPolicies.Upload);

        group.MapGet("/", async (DocumentService documents, ICurrentUser user, CancellationToken ct) =>
            Results.Ok(await documents.ListAsync(user.UserId, ct)));

        group.MapGet("/{id:guid}", async (Guid id, DocumentService documents, ICurrentUser user, CancellationToken ct) =>
            (await documents.ListAsync(user.UserId, ct)).FirstOrDefault(d => d.Id == id) is { } doc
                ? Results.Ok(doc) : Results.NotFound());

        group.MapGet("/{id:guid}/file", async (Guid id, DocumentService documents, IFileStorage storage,
            ICurrentUser user, CancellationToken ct) =>
            await documents.FindAsync(user.UserId, id, ct) is { } doc
                ? Results.File(storage.OpenRead(doc.StoragePath), "application/pdf", doc.FileName, enableRangeProcessing: true)
                : Results.NotFound());

        group.MapPost("/{id:guid}/retry", async (Guid id, DocumentService documents, ICurrentUser user, CancellationToken ct) =>
            await documents.RetryAsync(user.UserId, id, ct) ? Results.Accepted() : Results.NotFound());

        group.MapDelete("/{id:guid}", async (Guid id, DocumentService documents, ICurrentUser user, CancellationToken ct) =>
            await documents.DeleteAsync(user.UserId, id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
