using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Shared.Dto;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Persistence;

public class StartupRecoveryService : IHostedService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly SessionStore _store;
    private readonly ILogger<StartupRecoveryService> _logger;

    public StartupRecoveryService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        SessionStore store,
        ILogger<StartupRecoveryService> logger)
    {
        _dbFactory = dbFactory;
        _store     = store;
        _logger    = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Apply any pending migrations
        await db.Database.MigrateAsync(ct);

        // Enable WAL mode for concurrent reads alongside the single queue writer
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);

        // Patch sessions that were Active when the hub last shut down
        var patched = await db.ClaudeSessions
            .Where(s => s.Status == SessionStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.Disconnected), ct);

        if (patched > 0)
            _logger.LogInformation("Marked {Count} stale Active sessions as Disconnected on startup", patched);

        // Load sessions from the last 7 days into memory
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);

        var recentSessions = await db.ClaudeSessions
            .Include(s => s.Machine)
            .Where(s => s.LastActivityAt >= cutoff)
            .OrderBy(s => s.StartedAt)
            .ToListAsync(ct);

        _logger.LogInformation("Loading {Count} recent sessions from DB into memory", recentSessions.Count);

        foreach (var sessionEntity in recentSessions)
        {
            _store.EnsureAgentFromDb(sessionEntity.Machine);

            var lines = await db.StreamedLines
                .Where(l => l.SessionId == sessionEntity.SessionId)
                .OrderByDescending(l => l.Id)
                .Take(2000)
                .OrderBy(l => l.Id)
                .Select(l => new StreamedLine
                {
                    DbId               = l.Id,
                    Timestamp          = l.Timestamp,
                    SessionId          = l.SessionId,
                    Kind               = l.Kind,
                    Content            = l.Content,
                    IsContentTruncated = l.IsContentTruncated,
                    ToolName           = l.ToolName,
                })
                .ToListAsync(ct);

            _store.RestoreSessionFromDb(sessionEntity, lines);
        }

        _logger.LogInformation("Startup recovery complete");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
