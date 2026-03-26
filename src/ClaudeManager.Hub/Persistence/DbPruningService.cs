using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Persistence;

/// <summary>
/// Background service that deletes sessions (and their output lines, via cascade)
/// whose last activity is older than <see cref="RetentionDays"/> days.
/// Runs once shortly after startup, then every 24 hours.
/// </summary>
public class DbPruningService : BackgroundService
{
    public const int RetentionDays      = 30;
    public const int BuildRetentionDays = 90;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval     = TimeSpan.FromHours(24);

    private const int DeleteChunkSize = 200;

    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly ILogger<DbPruningService> _logger;

    public DbPruningService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        ILogger<DbPruningService> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(InitialDelay, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PruneAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB pruning run failed");
            }

            await Task.Delay(Interval, ct);
        }
    }

    internal async Task PruneAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Fetch stale session IDs client-side: SQLite cannot translate DateTimeOffset
        // comparisons in EF WHERE clauses.
        var allIds = await db.ClaudeSessions
            .Select(s => new { s.SessionId, s.LastActivityAt })
            .ToListAsync(ct);

        var staleIds = allIds
            .Where(s => s.LastActivityAt < cutoff)
            .Select(s => s.SessionId)
            .ToList();

        if (staleIds.Count == 0)
        {
            _logger.LogDebug("DB pruning: no sessions older than {RetentionDays} days", RetentionDays);
        }
        else
        {
            // Delete in chunks to avoid oversized IN clauses.
            // Cascade delete (configured on the DbContext model) removes StreamedLines automatically.
            var deleted = 0;
            for (var i = 0; i < staleIds.Count; i += DeleteChunkSize)
            {
                var chunk = staleIds.Skip(i).Take(DeleteChunkSize).ToList();
                deleted += await db.ClaudeSessions
                    .Where(s => chunk.Contains(s.SessionId))
                    .ExecuteDeleteAsync(ct);
            }

            _logger.LogInformation(
                "DB pruning: deleted {Count} session(s) older than {RetentionDays} days",
                deleted, RetentionDays);
        }

        await PruneBuildJobsAsync(db, ct);
    }

    private async Task PruneBuildJobsAsync(ClaudeManagerDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-BuildRetentionDays);

        var allJobs = await db.SweAfJobs
            .Select(j => new { j.Id, j.CreatedAt })
            .ToListAsync(ct);

        var staleIds = allJobs
            .Where(j => j.CreatedAt < cutoff)
            .Select(j => j.Id)
            .ToList();

        if (staleIds.Count == 0)
        {
            _logger.LogDebug("DB pruning: no build jobs older than {BuildRetentionDays} days",
                BuildRetentionDays);
            return;
        }

        var deleted = 0;
        for (var i = 0; i < staleIds.Count; i += DeleteChunkSize)
        {
            var chunk = staleIds.Skip(i).Take(DeleteChunkSize).ToList();
            deleted += await db.SweAfJobs
                .Where(j => chunk.Contains(j.Id))
                .ExecuteDeleteAsync(ct);
        }

        _logger.LogInformation(
            "DB pruning: deleted {Count} build job(s) older than {BuildRetentionDays} days",
            deleted, BuildRetentionDays);
    }
}
