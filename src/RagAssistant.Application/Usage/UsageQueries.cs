using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Abstractions;

namespace RagAssistant.Application.Usage;

public sealed record UsageTotals(int Requests, long InputTokens, long OutputTokens, decimal EstimatedCostUsd);
public sealed record UsageByOperation(string Operation, int Requests, long InputTokens, long OutputTokens, decimal EstimatedCostUsd);
public sealed record UsageByModel(string Provider, string Model, int Requests, long InputTokens, long OutputTokens, decimal EstimatedCostUsd);
public sealed record UsageByDay(DateOnly Day, int Requests, long InputTokens, long OutputTokens, decimal EstimatedCostUsd);
public sealed record UsageSummary(DateTimeOffset From, DateTimeOffset To, UsageTotals Totals,
    IReadOnlyList<UsageByOperation> ByOperation, IReadOnlyList<UsageByModel> ByModel, IReadOnlyList<UsageByDay> ByDay);

public sealed class UsageQueries(IAppDbContext db, TimeProvider time)
{
    public async Task<UsageSummary> SummaryAsync(Guid userId, int days, CancellationToken ct)
    {
        var to = time.GetUtcNow();
        var from = to.AddDays(-Math.Clamp(days, 1, 365));
        var rows = await db.LlmUsages.AsNoTracking()
            .Where(u => u.UserId == userId && u.CreatedAt >= from)
            .Select(u => new { u.Operation, u.Provider, u.Model, u.InputTokens, u.OutputTokens, u.EstimatedCostUsd, u.CreatedAt })
            .ToListAsync(ct);

        return new UsageSummary(from, to,
            new UsageTotals(rows.Count, rows.Sum(r => (long)r.InputTokens), rows.Sum(r => (long)r.OutputTokens), rows.Sum(r => r.EstimatedCostUsd)),
            rows.GroupBy(r => r.Operation)
                .Select(g => new UsageByOperation(g.Key, g.Count(), g.Sum(r => (long)r.InputTokens), g.Sum(r => (long)r.OutputTokens), g.Sum(r => r.EstimatedCostUsd)))
                .OrderByDescending(x => x.EstimatedCostUsd).ToList(),
            rows.GroupBy(r => (r.Provider, r.Model))
                .Select(g => new UsageByModel(g.Key.Provider, g.Key.Model, g.Count(), g.Sum(r => (long)r.InputTokens), g.Sum(r => (long)r.OutputTokens), g.Sum(r => r.EstimatedCostUsd)))
                .OrderByDescending(x => x.EstimatedCostUsd).ThenByDescending(x => x.Requests).ToList(),
            rows.GroupBy(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime))
                .Select(g => new UsageByDay(g.Key, g.Count(), g.Sum(r => (long)r.InputTokens), g.Sum(r => (long)r.OutputTokens), g.Sum(r => r.EstimatedCostUsd)))
                .OrderBy(x => x.Day).ToList());
    }
}
