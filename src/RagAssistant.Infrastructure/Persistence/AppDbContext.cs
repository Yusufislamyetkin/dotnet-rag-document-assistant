using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using RagAssistant.Application.Abstractions;
using RagAssistant.Domain;
using RagAssistant.Infrastructure.Identity;

namespace RagAssistant.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    /// <summary>Vector column dimension. Must match the embedding model (text-embedding-3-small = 1536).</summary>
    public const int EmbeddingDimensions = 1536;

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<LlmUsage> LlmUsages => Set<LlmUsage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasPostgresExtension("vector");

        b.Entity<Document>(e =>
        {
            e.ToTable("documents");
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.Property(x => x.StoragePath).HasMaxLength(500).IsRequired();
            e.Property(x => x.EmbeddingModel).HasMaxLength(100);
            e.HasIndex(x => new { x.UserId, x.Sha256 }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Chunks).WithOne(x => x.Document).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DocumentChunk>(e =>
        {
            e.ToTable("document_chunks");
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.Embedding)
                .HasColumnType($"vector({EmbeddingDimensions})")
                .HasConversion(v => v == null ? null : new Vector(v), v => v == null ? null : v.ToArray());
            e.HasIndex(x => new { x.DocumentId, x.ChunkIndex }).IsUnique();
            e.HasIndex(x => x.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
            // Generated tsvector column for PostgreSQL full-text search (not mapped to the domain model).
            e.Property<NpgsqlTypes.NpgsqlTsVector>("SearchVector")
                .HasColumnName("search_vector")
                .HasComputedColumnSql("to_tsvector('english', content)", stored: true);
            e.HasIndex("SearchVector").HasMethod("GIN");
        });

        b.Entity<ChatSession>(e =>
        {
            e.ToTable("chat_sessions");
            e.Property(x => x.Title).HasMaxLength(200);
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.CitationsJson).HasColumnName("citations").HasColumnType("jsonb");
            e.HasIndex(x => new { x.SessionId, x.CreatedAt });
        });

        b.Entity<LlmUsage>(e =>
        {
            e.ToTable("llm_usage");
            e.Property(x => x.Operation).HasMaxLength(16);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.EstimatedCostUsd).HasPrecision(12, 6);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
