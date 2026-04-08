using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Allocates ports from the configured range for per-build AgentField control plane containers.
/// Port availability is derived entirely from the DB — a port is "in use" while its job is non-terminal.
/// </summary>
public class SweAfPortAllocator
{
    /// <summary>
    /// Returns the first available port in [config.PortRangeStart, config.PortRangeEnd], or null
    /// if all ports are occupied by active (non-terminal) jobs.
    /// </summary>
    public async Task<int?> AllocatePortAsync(
        SweAfConfigEntity config,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var usedPorts = await db.SweAfJobs
            .Where(j => j.AllocatedPort != null
                     && j.Status != BuildStatus.Succeeded
                     && j.Status != BuildStatus.Failed
                     && j.Status != BuildStatus.Cancelled)
            .Select(j => j.AllocatedPort!.Value)
            .ToHashSetAsync(ct);

        for (var port = config.PortRangeStart; port <= config.PortRangeEnd; port++)
        {
            if (!usedPorts.Contains(port))
                return port;
        }

        return null;
    }
}
