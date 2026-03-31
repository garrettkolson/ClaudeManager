using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Orchestrates vLLM deployment lifecycle: CRUD, start/stop, and log retrieval.
/// Also triggers LLM proxy config updates via LlmDeploymentNotifier.
/// </summary>
public class LlmDeploymentService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly LlmInstanceService     _instance;
    private readonly GpuHostService         _gpuHosts;
    private readonly HubSecretService       _secrets;
    private readonly LlmDeploymentNotifier  _notifier;
    private readonly NginxProxyService      _nginxProxy;
    private readonly LlmProxyConfigService  _proxyConfig;
    private readonly ILogger<LlmDeploymentService> _logger;

    public LlmDeploymentService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        LlmInstanceService    instance,
        GpuHostService        gpuHosts,
        HubSecretService      secrets,
        LlmDeploymentNotifier notifier,
        NginxProxyService     nginxProxy,
        LlmProxyConfigService proxyConfig,
        ILogger<LlmDeploymentService> logger)
    {
        _dbFactory  = dbFactory;
        _instance   = instance;
        _gpuHosts   = gpuHosts;
        _secrets    = secrets;
        _notifier   = notifier;
        _nginxProxy = nginxProxy;
        _proxyConfig = proxyConfig;
        _logger     = logger;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<List<LlmDeploymentEntity>> GetAllAsync()
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.LlmDeployments
            .OrderBy(d => d.HostId)
            .ThenBy(d => d.ModelId)
            .ToListAsync();
    }

    public async Task<LlmDeploymentEntity> CreateAsync(
        string hostId, string modelId, string gpuIndices, int hostPort,
        string quantization = "none", string imageTag = "latest",
        string? extraArgs = null, string? hfTokenOverride = null,
        int? maxModelLen = null)
    {
        var entity = new LlmDeploymentEntity
        {
            DeploymentId    = Guid.NewGuid().ToString("N")[..12],
            HostId          = hostId,
            ModelId         = modelId.Trim(),
            GpuIndices      = gpuIndices.Trim(),
            HostPort        = hostPort,
            Quantization    = quantization,
            ImageTag        = string.IsNullOrWhiteSpace(imageTag) ? "latest" : imageTag.Trim(),
            ExtraArgs       = string.IsNullOrWhiteSpace(extraArgs) ? null : extraArgs.Trim(),
            HfTokenOverride = string.IsNullOrWhiteSpace(hfTokenOverride) ? null : hfTokenOverride.Trim(),
            MaxModelLen     = maxModelLen > 0 ? maxModelLen : null,
            Status         = LlmDeploymentStatus.Stopped,
            CreatedAt      = DateTimeOffset.UtcNow,
        };

        await using var db = _dbFactory.CreateDbContext();
        db.LlmDeployments.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        // Stop the container if it's running before removing from DB
        var deployment = await FindAsync(id);
        if (deployment is not null && deployment.ContainerId is not null
            && deployment.Status is LlmDeploymentStatus.Running or LlmDeploymentStatus.Starting)
        {
            var host = await _gpuHosts.GetByHostIdAsync(deployment.HostId);
            if (host is not null)
                await _instance.StopContainerAsync(host, deployment.ContainerId, ct);
        }

        await using var db = _dbFactory.CreateDbContext();
        var entity = await db.LlmDeployments.FindAsync([id], ct);
        if (entity is not null)
        {
            db.LlmDeployments.Remove(entity);
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the vLLM Docker container for the given deployment.
    /// Returns null on success or an error message on failure.
    /// </summary>
    public async Task<string?> StartAsync(long id, CancellationToken ct = default)
    {
        var deployment = await FindAsync(id);
        if (deployment is null) return "Deployment not found.";

        var host = await _gpuHosts.GetByHostIdAsync(deployment.HostId);
        if (host is null) return $"GPU host '{deployment.HostId}' not found.";

        // Resolve HF token: per-deployment override takes precedence
        var hfToken = !string.IsNullOrWhiteSpace(deployment.HfTokenOverride)
            ? deployment.HfTokenOverride
            : await _secrets.GetAsync(HubSecretService.HuggingFaceTokenKey);

        // Transition to Starting (no nginx refresh needed — container not yet running)
        await SetStatusAsync(deployment, LlmDeploymentStatus.Starting, containerId: null, error: null);

        var (containerId, error) = await _instance.StartContainerAsync(host, deployment, hfToken, ct);

        if (error is not null)
        {
            await SetStatusAsync(deployment, LlmDeploymentStatus.Error, containerId: null, error: error, ct);
            return error;
        }

        deployment.ContainerId = containerId;

        // (C) Startup health poll — wait for vLLM to be ready before marking Running
        _logger.LogInformation(
            "Waiting for vLLM startup health check on deployment {Id}...", deployment.Id);

        var startupCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupCt.CancelAfter(TimeSpan.FromSeconds(120)); // 2 minute timeout for model load

        var healthPollInterval = TimeSpan.FromSeconds(2);
        var startTime = DateTimeOffset.UtcNow;
        var isHealthy = false;

        while (!isHealthy && !startupCt.Token.IsCancellationRequested)
        {
            try
            {
                isHealthy = await _instance.CheckHealthAsync(host, deployment.HostPort, startupCt.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Health poll failed during startup: {Error}", ex.Message);
            }

            if (!isHealthy)
            {
                await Task.Delay(healthPollInterval, startupCt.Token);
            }
        }

        if (!isHealthy)
        {
            var elapsed = (DateTimeOffset.UtcNow - startTime).TotalSeconds;
            _logger.LogWarning(
                "Startup health check timed out after {Elapsed}s for deployment {Id}",
                elapsed, deployment.Id);

            // Clean up the container
            await _instance.StopContainerAsync(host, containerId, ct);

            await SetStatusAsync(deployment, LlmDeploymentStatus.Error,
                containerId: null,
                error: $"Startup health check failed after {elapsed:F0}s",
                ct);
            return $"Startup health check failed after {elapsed:F0}s";
        }

        _logger.LogInformation(
            "vLLM startup health check passed in {Elapsed:F0}s for deployment {Id}",
            (DateTimeOffset.UtcNow - startTime).TotalSeconds, deployment.Id);

        deployment.StartedAt = DateTimeOffset.UtcNow;
        deployment.RestartCount = 0; // Reset on manual start
        deployment.LastHealthCheckAt = DateTimeOffset.UtcNow;
        await SetStatusAsync(deployment, LlmDeploymentStatus.Running, containerId, error: null, ct);
        return null;
    }

    /// <summary>
    /// Stops the vLLM Docker container.
    /// Returns null on success or an error message on failure.
    /// </summary>
    public async Task<string?> StopAsync(long id, CancellationToken ct = default)
    {
        var deployment = await FindAsync(id);
        if (deployment is null) return "Deployment not found.";

        if (deployment.ContainerId is null)
        {
            await SetStatusAsync(deployment, LlmDeploymentStatus.Stopped, containerId: null, error: null, ct);
            return null;
        }

        var host = await _gpuHosts.GetByHostIdAsync(deployment.HostId);
        if (host is null) return $"GPU host '{deployment.HostId}' not found.";

        var error = await _instance.StopContainerAsync(host, deployment.ContainerId, ct);

        await SetStatusAsync(deployment, LlmDeploymentStatus.Stopped,
            containerId: null,
            error: error is not null ? $"Stop warning: {error}" : null,
            ct);

        return error;
    }

    // ── Logs ──────────────────────────────────────────────────────────────────

    public async Task<(string? Logs, string? Error)> GetLogsAsync(
        long id, int lines = 200, CancellationToken ct = default)
    {
        var deployment = await FindAsync(id);
        if (deployment is null) return (null, "Deployment not found.");

        if (deployment.ContainerId is null)
            return (null, "No container is associated with this deployment (never started).");

        var host = await _gpuHosts.GetByHostIdAsync(deployment.HostId);
        if (host is null) return (null, $"GPU host '{deployment.HostId}' not found.");

        return await _instance.GetLogsAsync(host, deployment.ContainerId, lines, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<LlmDeploymentEntity?> FindAsync(long id)
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.LlmDeployments.FindAsync(id);
    }

    private async Task SetStatusAsync(LlmDeploymentEntity deployment,
        LlmDeploymentStatus status, string? containerId, string? error,
        CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var entity = await db.LlmDeployments.FindAsync([deployment.Id], ct);
        if (entity is null) return;

        entity.Status       = status;
        entity.ErrorMessage = error;
        if (containerId is not null) entity.ContainerId = containerId;
        if (status == LlmDeploymentStatus.Stopped) entity.ContainerId = null;

        // Also save RestartCount and LastHealthCheckAt if they were set by the caller
        entity.RestartCount = deployment.RestartCount;
        entity.LastHealthCheckAt = deployment.LastHealthCheckAt;

        await db.SaveChangesAsync(ct);

        // Reflect changes back to the in-memory reference for the caller
        deployment.Status       = entity.Status;
        deployment.ErrorMessage = entity.ErrorMessage;
        deployment.ContainerId  = entity.ContainerId;

        _notifier.NotifyChanged(entity);

        // Update the nginx proxy config whenever the running set may have changed.
        // Errors are non-fatal — logged as warnings so they don't block the deployment operation.
        await RefreshNginxConfigAsync(deployment.HostId, ct);
    }

    private async Task RefreshNginxConfigAsync(string hostId, CancellationToken ct)
    {
        var host = await _gpuHosts.GetByHostIdAsync(hostId);
        if (host?.ProxyPort is null) return;

        await using var db = _dbFactory.CreateDbContext();
        var running = await db.LlmDeployments
            .Where(d => d.HostId == hostId && d.Status == LlmDeploymentStatus.Running)
            .ToListAsync(ct);

        var error = await _nginxProxy.ApplyConfigAsync(host, running, ct);
        if (error is not null)
            _logger.LogWarning("Nginx proxy update failed for host {HostId}: {Error}", hostId, error);

        // Notify that the proxy config may have changed (for REST cache refresh + SignalR broadcast)
        _proxyConfig.InvalidateCache();
    }
}
