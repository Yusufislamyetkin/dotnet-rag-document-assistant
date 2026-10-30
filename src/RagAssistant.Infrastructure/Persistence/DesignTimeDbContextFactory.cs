using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RagAssistant.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef migrations</c> only.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DependencyInjection.ConfigureDb(options, "Host=localhost;Port=5433;Database=ragassistant;Username=rag;Password=rag");
        return new AppDbContext(options.Options);
    }
}
