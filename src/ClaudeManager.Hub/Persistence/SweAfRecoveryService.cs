using ClaudeManager.Hub.Services;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Persistence;

/// <summary>
/// Runs once at startup and reconciles any SWE-AF jobs that were in-flight when
/// the hub last shut down. Polls AgentField directly for their current status so
/// the dashboard reflects reality even if webhook deliveries were missed during
/// the downtime window.
/// </summary>
public class SweAfRecoveryService : IHostedService
{
    private readonly SweAfService _svc;
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly ILogger<SweAfRecoveryService> _logger;

    public SweAfRecoveryService(
        SweAfService svc,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        ILogger<SweAfRecoveryService> logger)
    {
        _svc       = svc;
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_svc.IsConfigured)
        {
            _logger.LogDebug("SWE-AF not configured; skipping build recovery");
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var all = await db.SweAfJobs.ToListAsync(ct);

        var inFlightIds = all
            .Where(j => j.Status is BuildStatus.Queued or BuildStatus.Running or BuildStatus.Waiting)
            .Select(j => j.ExternalJobId)
            .ToList();

        if (inFlightIds.Count == 0)
        {
            _logger.LogDebug("SWE-AF recovery: no in-flight builds to reconcile");
            return;
        }

        _logger.LogInformation("SWE-AF recovery: reconciling {Count} in-flight build(s)", inFlightIds.Count);
        await _svc.ReconcileAsync(inFlightIds, ct);
        _logger.LogInformation("SWE-AF recovery complete");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
