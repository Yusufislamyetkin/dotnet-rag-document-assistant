using Microsoft.EntityFrameworkCore;
using RagAssistant.Domain;

namespace RagAssistant.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Document> Documents { get; }
    DbSet<DocumentChunk> DocumentChunks { get; }
    DbSet<ChatSession> ChatSessions { get; }
    DbSet<ChatMessage> ChatMessages { get; }
    DbSet<LlmUsage> LlmUsages { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    void ChangeTrackerClear();
}
