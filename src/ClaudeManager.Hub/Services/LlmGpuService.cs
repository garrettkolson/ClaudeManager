using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Provides live GPU monitoring metrics for GPU hosts with auto-refresh every 5 seconds.
/// Uses BackgroundService pattern for periodic metric refresh.
/// </summary>
public class LlmGpuService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<LlmGpuService> _logger;
    private readonly ConcurrentDictionary<long, (GpuMetrics[] Gpus, string? Error, DateTimeOffset LastUpdated)> _metricsCache = new();
    private readonly object _lock = new(); // For safe parallel access

    private CancellationTokenSource _cancelTokenSource = new();
    private Task? _backgroundTask;

    public LlmGpuService(ILogger<LlmGpuService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LlmGpuService starting GPU metric refresh loop");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var hosts = await GetAllHostsAsync(stoppingToken);

                    if (hosts.Any())
                    {
                        await RefreshMetricsForAllHostsAsync(hosts, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("LlmGpuService refresh cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during GPU metric refresh");
                }

                await Task.Delay(RefreshInterval, stoppingToken);
            }
        }
        finally
        {
            _cancelTokenSource.Cancel();
        }
    }

    private async Task<List<GpuHostEntity>> GetAllHostsAsync(CancellationToken ct = default)
    {
        await using var db = new ClaudeManagerDbContext();
        return await db.GpuHosts.ToListAsync(ct);
    }

    private async Task RefreshMetricsForAllHostsAsync(List<GpuHostEntity> hosts, CancellationToken ct)
    {
        var refreshTasks = hosts.Select(async host =>
        {
            try
            {
                var (gpusRaw, error) = await LlmGpuDiscoveryService.DiscoverAsync(host, ct);
                if (error is not null)
                {
                    lock (_lock)
                    {
                        _metricsCache[host.Id] = (null, error, DateTimeOffset.UtcNow);
                    }
                    return;
                }

                // Calculate availability: (total - free) / total * 100
                var availabilityPct = gpusRaw.Length > 0
                    ? gpusRaw.Average(g => (double)(g.MemoryTotalMb - g.MemoryFreeMb) / g.MemoryTotalMb * 100)
                    : 0;

                // Estimate utilization as (100 - availability) % - this is rough since we don't have real utilization data
                // In a full implementation, this would come from nvidia-smi --query-gpu=utilization.gpu
                var estimatedUtilPct = (int)(100 - availabilityPct);

                var gpus = gpusRaw.Select(info => new GpuMetrics(
                    info.Index,
                    info.Name,
                    info.MemoryTotalMb,
                    info.MemoryFreeMb,
                    estimatedUtilPct)).ToArray();

                lock (_lock)
                {
                    _metricsCache[host.Id] = (gpus, null, DateTimeOffset.UtcNow);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh metrics for host {HostId}", host.HostId);
                lock (_lock)
                {
                    _metricsCache[host.Id] = (null, $"SSH/connection failure: {ex.Message}", DateTimeOffset.UtcNow);
                }
            }
        });

        await Task.WhenAll(refreshTasks);
    }

    /// <summary>
    /// Gets GPU metrics for a specific host.
    /// </summary>
    public async Task<(IReadOnlyList<GpuMetrics> Gpus, string? Error, DateTimeOffset LastUpdated)> GetAsync(
        long hostId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_metricsCache.TryGetValue(hostId, out var cached) || cached.Gpus is null)
            {
                throw new InvalidOperationException("No metrics available for this host. Ensure at least one GPU host is configured and metrics have been refreshed (auto-refresh runs every 5 seconds).");
            }

            return (cached.Gpus, cached.Error, cached.LastUpdated);
        }
    }

    /// <summary>
    /// Gets GPU metrics for all hosts.
    /// </summary>
    public async Task<Dictionary<long, (IReadOnlyList<GpuMetrics> Gpus, string? Error, DateTimeOffset LastUpdated)>> GetAllAsync(
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, (IReadOnlyList<GpuMetrics> Gpus, string? Error, DateTimeOffset LastUpdated)>();

        lock (_lock)
        {
            result = _metricsCache.Select(kvp => (kvp.Key, kvp.Value)).ToDictionary(x => x.Item1);
        }

        // Refresh all metrics in parallel if there's data
        await Task.Run(async () =>
        {
            var hosts = await GetAllHostsAsync(ct);
            if (!hosts.Any()) return;

            var refreshTasks = hosts.Select(async host =>
            {
                try
                {
                    var (gpusRaw, error) = await LlmGpuDiscoveryService.DiscoverAsync(host, ct);
                    if (error is not null)
                    {
                        return;
                    }

                    var availabilityPct = gpusRaw.Length > 0
                        ? gpusRaw.Average(g => (double)(g.MemoryTotalMb - g.MemoryFreeMb) / g.MemoryTotalMb * 100)
                        : 0;

                    var estimatedUtilPct = (int)(100 - availabilityPct);
                    var gpus = gpusRaw.Select(info => new GpuMetrics(
                        info.Index,
                        info.Name,
                        info.MemoryTotalMb,
                        info.MemoryFreeMb,
                        estimatedUtilPct)).ToArray();

                    lock (_lock)
                    {
                        _metricsCache[host.Id] = (gpus, null, DateTimeOffset.UtcNow);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to refresh metrics for host {HostId}", host.HostId);
                    lock (_lock)
                    {
                        var stale = _metricsCache[host.Id];
                        _metricsCache[host.Id] = (stale.Gpus ?? [], $"SSH/connection failure: {ex.Message}", stale.LastUpdated);
                    }
                }
            });

            await Task.WhenAll(refreshTasks);
        });

        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancelTokenSource?.Cancel();
            _cancelTokenSource?.Dispose();
        }

        base.Dispose(disposing);
    }
}
