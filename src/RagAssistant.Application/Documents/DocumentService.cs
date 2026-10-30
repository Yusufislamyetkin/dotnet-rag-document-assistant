using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain;

namespace RagAssistant.Application.Documents;

public sealed record DocumentDto(
    Guid Id, string FileName, long SizeBytes, int PageCount, string Status, string? ErrorMessage,
    DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt, int ChunkCount)
{
    public static DocumentDto From(Document d, int chunkCount = 0) => new(d.Id, d.FileName, d.SizeBytes, d.PageCount,
        d.Status.ToString(), d.ErrorMessage, d.CreatedAt, d.ProcessedAt, chunkCount);
}

public enum UploadOutcome { Created, Duplicate, Invalid }

public sealed record UploadResult(string FileName, UploadOutcome Outcome, DocumentDto? Document, string? Error);

public sealed class DocumentService(
    IAppDbContext db,
    IFileStorage storage,
    IIngestionQueue queue,
    IOptions<IngestionOptions> options)
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    public async Task<UploadResult> UploadAsync(Guid userId, string fileName, Stream content, long length, CancellationToken ct)
    {
        var name = Path.GetFileName(fileName ?? "").Trim();
        if (name.Length == 0 || name.Length > 255)
            return Invalid(name, "File name is missing or longer than 255 characters.");
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Invalid(name, "Only PDF files are supported.");
        if (length <= 0)
            return Invalid(name, "File is empty.");
        if (length > options.Value.MaxFileBytes)
            return Invalid(name, $"File exceeds the {options.Value.MaxFileBytes / (1024 * 1024)} MB limit.");

        // Buffer once: needed for the magic-byte check, the hash and the save.
        using var buffer = new MemoryStream((int)length);
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > options.Value.MaxFileBytes)
            return Invalid(name, $"File exceeds the {options.Value.MaxFileBytes / (1024 * 1024)} MB limit.");
        if (!buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, PdfMagic.Length)).SequenceEqual(PdfMagic))
            return Invalid(name, "File is not a valid PDF.");

        var sha = Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        var existing = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.UserId == userId && d.Sha256 == sha, ct);
        if (existing is not null)
            return new UploadResult(name, UploadOutcome.Duplicate, DocumentDto.From(existing), "This file was already uploaded.");

        var doc = new Document { UserId = userId, FileName = name, Sha256 = sha, SizeBytes = buffer.Length };
        buffer.Position = 0;
        doc.StoragePath = await storage.SaveAsync(userId, doc.Id, buffer, ct);
        db.Documents.Add(doc);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent upload of the same file won the unique (user_id, sha256) index.
            storage.Delete(doc.StoragePath);
            db.ChangeTrackerClear();
            var winner = await db.Documents.AsNoTracking().FirstAsync(d => d.UserId == userId && d.Sha256 == sha, ct);
            return new UploadResult(name, UploadOutcome.Duplicate, DocumentDto.From(winner), "This file was already uploaded.");
        }

        await queue.EnqueueAsync(doc.Id, ct);
        return new UploadResult(name, UploadOutcome.Created, DocumentDto.From(doc), null);
    }

    public async Task<IReadOnlyList<DocumentDto>> ListAsync(Guid userId, CancellationToken ct) =>
        await db.Documents.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DocumentDto(d.Id, d.FileName, d.SizeBytes, d.PageCount, d.Status.ToString(), d.ErrorMessage,
                d.CreatedAt, d.ProcessedAt, d.Chunks.Count))
            .ToListAsync(ct);

    public Task<Document?> FindAsync(Guid userId, Guid id, CancellationToken ct) =>
        db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct);

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct);
        if (doc is null) return false;
        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        storage.Delete(doc.StoragePath);
        return true;
    }

    public async Task<bool> RetryAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct);
        if (doc is null || doc.Status != DocumentStatus.Failed) return false;
        doc.Status = DocumentStatus.Uploaded;
        doc.ErrorMessage = null;
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(doc.Id, ct);
        return true;
    }

    private static UploadResult Invalid(string name, string error) => new(name, UploadOutcome.Invalid, null, error);
}
