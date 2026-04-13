using ClaudeManager.Hub.Services;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Persistence;

/// <summary>
/// Runs once at startup to:
/// 1. Reconcile any SWE-AF jobs that were in-flight when the hub last shut down.
/// 2. Tear down any per-build control plane containers left over from jobs that are now terminal.
/// </summary>
public class SweAfRecoveryService(
    SweAfService svc,
    ISwarmProvisioningService provisioningSvc,
    IDbContextFactory<ClaudeManagerDbContext> dbFactory,
    ILogger<SweAfRecoveryService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!svc.IsConfigured)
        {
            logger.LogDebug("SWE-AF not configured; skipping build recovery");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.SweAfJobs.ToListAsync(ct);

        // Pass 1: reconcile in-flight builds against AgentField
        var inFlightIds = all
            .Where(j => j.Status is BuildStatus.Queued or BuildStatus.Running or BuildStatus.Waiting)
            .Select(j => j.ExternalJobId)
            .ToList();

        if (inFlightIds.Count > 0)
        {
            logger.LogInformation("SWE-AF recovery: reconciling {Count} in-flight build(s)", inFlightIds.Count);
            await svc.ReconcileAsync(inFlightIds, ct);
            logger.LogInformation("SWE-AF recovery: reconciliation complete");
        }
        else
        {
            logger.LogDebug("SWE-AF recovery: no in-flight builds to reconcile");
        }

        // Pass 2: clean up orphaned per-build containers
        await CleanUpOrphanedContainersAsync(all.Select(j => (j.Id, j.Status, j.ComposeProjectName)).ToList(), ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task CleanUpOrphanedContainersAsync(
        List<(long Id, BuildStatus Status, string? ComposeProjectName)> allJobs,
        CancellationToken ct)
    {
        List<string> runningProjects;
        try
        {
            runningProjects = await provisioningSvc.ListActiveComposeProjectsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list compose projects during recovery; skipping orphan cleanup");
            return;
        }

        if (runningProjects.Count == 0)
            return;

        var runningSet = new HashSet<string>(runningProjects, StringComparer.OrdinalIgnoreCase);

        // Tear down containers belonging to terminal jobs
        var terminalWithContainers = allJobs
            .Where(j => j.Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Cancelled
                     && !string.IsNullOrWhiteSpace(j.ComposeProjectName)
                     && runningSet.Contains(j.ComposeProjectName!));

        foreach (var (id, _, projectName) in terminalWithContainers)
        {
            logger.LogInformation(
                "Recovery: tearing down orphaned container for terminal job {JobId} (project={Project})",
                id, projectName);
            var error = await provisioningSvc.StopControlPlaneForJobAsync(projectName!, ct);
            if (error is not null)
                logger.LogWarning("Recovery teardown of {Project} failed: {Error}", projectName, error);
        }

        // Tear down containers with no matching job in the DB at all
        var knownProjects = new HashSet<string>(
            allJobs.Where(j => j.ComposeProjectName is not null).Select(j => j.ComposeProjectName!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var project in runningProjects)
        {
            if (!knownProjects.Contains(project))
            {
                logger.LogWarning(
                    "Recovery: found orphaned compose project {Project} with no matching job — tearing down",
                    project);
                var error = await provisioningSvc.StopControlPlaneForJobAsync(project, ct);
                if (error is not null)
                    logger.LogWarning("Recovery teardown of orphan {Project} failed: {Error}", project, error);
            }
        }
    }
}
