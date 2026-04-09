using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Allocates ports from the configured range for per-build AgentField control plane containers.
/// Port availability is derived entirely from the DB — a port is "in use" while its job is non-terminal.
/// Each build uses a block of 3 consecutive ports: control-plane, swe-agent, swe-fast.
/// </summary>
public class SweAfPortAllocator
{
    /// <summary>Number of consecutive ports required per build.</summary>
    public const int BlockSize = 3;

    /// <summary>
    /// Returns the first available base port in [config.PortRangeStart, config.PortRangeEnd]
    /// where the entire 3-port block (base, base+1, base+2) is free, or null if no block is available.
    /// </summary>
    public async Task<int?> AllocatePortAsync(
        SweAfConfigEntity config,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var allocatedBases = await db.SweAfJobs
            .Where(j => j.AllocatedPort != null
                     && j.Status != BuildStatus.Succeeded
                     && j.Status != BuildStatus.Failed
                     && j.Status != BuildStatus.Cancelled)
            .Select(j => j.AllocatedPort!.Value)
            .ToListAsync(ct);

        // Expand each allocated base port into its full block so we can check all offsets
        var usedPorts = new HashSet<int>();
        foreach (var basePort in allocatedBases)
            for (var i = 0; i < BlockSize; i++)
                usedPorts.Add(basePort + i);

        // Find the first base port where the entire block is free
        for (var port = config.PortRangeStart; port <= config.PortRangeEnd - (BlockSize - 1); port++)
        {
            var blockFree = true;
            for (var i = 0; i < BlockSize; i++)
            {
                if (!usedPorts.Contains(port + i)) continue;
                blockFree = false;
                break;
            }
            if (blockFree) return port;
        }

        return null;
    }
}
