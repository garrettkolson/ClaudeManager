using Microsoft.AspNetCore.SignalR;
using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Computes and caches the LLM proxy config for agents.
/// Invalidates cache on deployment changes and broadcasts via SignalR.
/// </summary>
public class LlmProxyConfigService
{
    private readonly GpuHostService          _gpuHosts;
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly IHubContext<AgentHub>   _hubContext;
    private readonly ILogger<LlmProxyConfigService> _logger;

    private object _cacheLock = new();
    private LlmProxyConfig _cachedConfig = new("");
    private DateTimeOffset _cacheTime = DateTimeOffset.MinValue;

    public LlmProxyConfigService(
        GpuHostService gpuHosts,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        IHubContext<AgentHub> hubContext,
        ILogger<LlmProxyConfigService> logger)
    {
        _gpuHosts  = gpuHosts;
        _dbFactory = dbFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Invalidates the cached config and broadcasts the new one to connected agents.
    /// Call this after nginx proxy changes.
    /// </summary>
    public void InvalidateCache()
    {
        var config = ComputeConfig();
        lock (_cacheLock)
        {
            _cachedConfig = config;
            _cacheTime    = DateTimeOffset.UtcNow;
        }

        _hubContext.Clients.All.SendAsync("llm-proxy-config", config);
    }

    /// <summary>
    /// Gets the current proxy config, computing it if necessary.
    /// </summary>
    public async Task<LlmProxyConfig> GetConfigAsync(CancellationToken ct = default)
    {
        lock (_cacheLock)
        {
            if (_cacheTime > DateTimeOffset.UtcNow.AddMinutes(-1))
                return _cachedConfig;
        }

        var config = await ComputeConfigAsync(ct);
        lock (_cacheLock)
        {
            _cachedConfig = config;
            _cacheTime    = DateTimeOffset.UtcNow;
        }
        return config;
    }

    /// <summary>
    /// Synchronous config computation for background threads.
    /// </summary>
    public LlmProxyConfig ComputeConfig()
    {
        return Task.Run(() => ComputeConfigAsync(CancellationToken.None)).GetAwaiter().GetResult();
    }

    private async Task<LlmProxyConfig> ComputeConfigAsync(CancellationToken ct)
    {
        var hosts = await _gpuHosts.GetAllAsync();
        var hostWithProxy = hosts.FirstOrDefault(h => h.ProxyPort.HasValue);

        if (hostWithProxy is null)
            return new LlmProxyConfig(ProxyUrl: "");

        await using var db = _dbFactory.CreateDbContext();
        var runningModels = await db.LlmDeployments
            .Where(d => d.HostId == hostWithProxy.HostId && d.Status == LlmDeploymentStatus.Running)
            .Select(d => d.ModelId)
            .Distinct()
            .ToListAsync(ct);

        var proxyUrl = $"http://{hostWithProxy.Host}:{hostWithProxy.ProxyPort}";
        return new LlmProxyConfig(ProxyUrl: proxyUrl);
    }
}
