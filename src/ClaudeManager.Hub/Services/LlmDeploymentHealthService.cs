using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Background service that periodically checks the health of Running LLM deployments.
/// Implements:
/// - (A) HTTP health polling with auto-restart on failure
/// - (B) Container state inspection to detect exited/missing containers
/// </summary>
public class LlmDeploymentHealthService : BackgroundService
{
    public const int MaxAutoRestarts = 3;
    private const int PollIntervalSeconds = 30;
    private const int HealthCheckTimeoutSeconds = 5;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(PollIntervalSeconds);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10); // Wait for startup recovery

    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly ILlmInstanceService _instance;
    private readonly GpuHostService _gpuHosts;
    private readonly HubSecretService _secrets;
    private readonly NginxProxyService _nginxProxy;
    private readonly LlmDeploymentNotifier _notifier;
    private readonly LlmProxyConfigService _proxyConfig;
    private readonly ILogger<LlmDeploymentHealthService> _logger;

    public LlmDeploymentHealthService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        ILlmInstanceService instance,
        GpuHostService gpuHosts,
        HubSecretService secrets,
        NginxProxyService nginxProxy,
        LlmDeploymentNotifier notifier,
        LlmProxyConfigService proxyConfig,
        ILogger<LlmDeploymentHealthService> logger)
    {
        _dbFactory = dbFactory;
        _instance = instance;
        _gpuHosts = gpuHosts;
        _secrets = secrets;
        _nginxProxy = nginxProxy;
        _notifier = notifier;
        _proxyConfig = proxyConfig;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("LlmDeploymentHealthService starting with {Interval}s interval", PollIntervalSeconds);
        await Task.Delay(InitialDelay, ct); // Wait for startup recovery

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await HealthCheckLoopAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check loop iteration failed");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task HealthCheckLoopAsync(CancellationToken ct)
    {
        await using var db = _dbFactory.CreateDbContext();
        var deployments = await db.LlmDeployments
            .Where(d => d.Status == LlmDeploymentStatus.Running
                     || (d.Status == LlmDeploymentStatus.Starting && d.ContainerId != null))
            .ToListAsync(ct);

        _logger.LogDebug("Checking health of {Count} active deployments", deployments.Count);

        foreach (var deployment in deployments)
        {
            if (ct.IsCancellationRequested) break;

            if (deployment.Status == LlmDeploymentStatus.Starting)
                await CheckStartingDeploymentAsync(deployment, ct);
            else
                await CheckSingleDeploymentAsync(deployment, ct);
        }
    }

    /// <summary>
    /// Monitors a Starting deployment while its model loads.
    /// Transitions to Running when vLLM begins serving, or to Error if the container exits.
    /// </summary>
    private async Task CheckStartingDeploymentAsync(LlmDeploymentEntity deployment, CancellationToken ct)
    {
        var host = await _gpuHosts.GetByHostIdAsync(deployment.HostId);
        if (host is null || deployment.ContainerId is null) return;

        _logger.LogDebug("Checking startup progress for deployment {Id} on {Host}", deployment.Id, host.Host);

        // Check whether the container is still alive
        var containerStatus = await _instance.InspectContainerAsync(host, deployment.ContainerId, ct);
        if (containerStatus is null || containerStatus != ContainerStatus.Running)
        {
            _logger.LogWarning("Deployment {Id} container exited during model load (status: {Status})",
                deployment.Id, containerStatus?.ToString() ?? "not found");
            await TransitionToErrorAsync(deployment,
                $"Container exited during startup (status: {containerStatus?.ToString() ?? "not found"})", ct);
            return;
        }

        // Check whether vLLM is now serving
        using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        httpCts.CancelAfter(TimeSpan.FromSeconds(HealthCheckTimeoutSeconds));

        var isHealthy = await _instance.CheckHealthAsync(host, deployment.HostPort, httpCts.Token);
        if (!isHealthy) return; // Still loading — will check again next interval

        deployment.Status       = LlmDeploymentStatus.Running;
        deployment.StartedAt    = DateTimeOffset.UtcNow;
        deployment.ErrorMessage = null;
        deployment.LastHealthCheckAt = DateTimeOffset.UtcNow;
        await SaveAndNotifyAsync(deployment, ct);
        await RefreshNginxConfigAsync(deployment.HostId, ct);

        _logger.LogInformation("Deployment {Id} transitioned Starting → Running after model load", deployment.Id);
    }

    private async Task CheckSingleDeploymentAsync(LlmDeploymentEntity deployment, CancellationToken ct)
    {
        var host = await _gpuHosts.GetByHostIdAsync(deployment.HostId);
        if (host is null || deployment.ContainerId is null)
        {
            _logger.LogWarning(
                "Deployment {Id} has Running status but missing host or container ID. Transitioning to Error.",
                deployment.Id);
            await TransitionToErrorAsync(deployment, "Missing host or container ID", ct);
            return;
        }

        _logger.LogDebug("Checking health of deployment {Id} on {Host}", deployment.Id, host.Host);

        // Step 1: Container state check (B) — fastest, catches exited containers
        var containerStatus = await _instance.InspectContainerAsync(host, deployment.ContainerId, ct);

        if (containerStatus is null)
        {
            // Container not found — was removed externally or crashed
            await HandleUnhealthyAsync(deployment, host, "Container not found", ct);
            return;
        }

        if (containerStatus != ContainerStatus.Running)
        {
            // Container exited or dead — still pass the old container ID for cleanup
            await HandleUnhealthyAsync(deployment, host, $"Container {containerStatus}", ct);
            return;
        }

        // Step 2: HTTP health check (A) — verifies FastAPI is actually responding
        var httpHealthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        httpHealthCts.CancelAfter(TimeSpan.FromSeconds(HealthCheckTimeoutSeconds));

        var isHealthy = await _instance.CheckHealthAsync(host, deployment.HostPort, httpHealthCts.Token);

        if (!isHealthy)
        {
            await HandleUnhealthyAsync(deployment, host, "HTTP health check failed", ct);
            return;
        }

        // Healthy — update LastHealthCheckAt
        deployment.LastHealthCheckAt = DateTimeOffset.UtcNow;
        await SaveAndNotifyAsync(deployment, ct);
    }

    public async Task HandleUnhealthyAsync(
        LlmDeploymentEntity deployment, GpuHostEntity host, string reason, CancellationToken ct)
    {
        _logger.LogWarning(
            "Deployment {Id} on {Host} is unhealthy: {Reason}. Restart count: {RestartCount}/{Max}",
            deployment.Id, host.Host, reason, deployment.RestartCount, MaxAutoRestarts);

        if (deployment.RestartCount < MaxAutoRestarts)
        {
            // Auto-restart
            deployment.RestartCount++;
            await SaveAndNotifyAsync(deployment, ct);

            _logger.LogInformation("Auto-restarting deployment {Id}...", deployment.Id);

            // Stop the container
            if (deployment.ContainerId is not null)
                await _instance.StopContainerAsync(host, deployment.ContainerId, ct);

            // Get HF token
            var hfToken = !string.IsNullOrWhiteSpace(deployment.HfTokenOverride)
                ? deployment.HfTokenOverride
                : await _secrets.GetAsync(HubSecretService.HuggingFaceTokenKey);

            // Start fresh container
            var (newContainerId, error) = await _instance.StartContainerAsync(host, deployment, hfToken, ct);

            if (error is not null)
            {
                // Restart failed — transition to Error
                await TransitionToErrorAsync(deployment, $"Restart failed: {error}", ct);
            }
            else
            {
                // Restart succeeded — wait for health before marking Running
                await Task.Delay(TimeSpan.FromSeconds(10), ct); // Give it time to start

                var isHealthy = await _instance.CheckHealthAsync(host, deployment.HostPort, ct);
                if (isHealthy)
                {
                    deployment.ContainerId = newContainerId;
                    deployment.Status = LlmDeploymentStatus.Running;
                    deployment.ErrorMessage = null;
                    deployment.LastHealthCheckAt = DateTimeOffset.UtcNow;
                    await SaveAndNotifyAsync(deployment, ct);

                    // Refresh nginx proxy config
                    await RefreshNginxConfigAsync(host.HostId, ct);

                    _logger.LogInformation("Auto-restart succeeded for deployment {Id}", deployment.Id);
                }
                else
                {
                    // Still unhealthy after restart — transition to Error
                    await TransitionToErrorAsync(deployment, "Failed health check after restart", ct);
                }
            }
        }
        else
        {
            // Max retries exceeded — transition to Error
            _logger.LogWarning(
                "Deployment {Id} exceeded max auto-restarts ({Max}). Transitioning to Error.",
                deployment.Id, MaxAutoRestarts);

            await TransitionToErrorAsync(deployment, $"Unhealthy: {reason} (max retries exceeded)", ct);
        }
    }

    private async Task TransitionToErrorAsync(LlmDeploymentEntity deployment, string error, CancellationToken ct)
    {
        deployment.Status = LlmDeploymentStatus.Error;
        deployment.ErrorMessage = error;
        deployment.LastHealthCheckAt = DateTimeOffset.UtcNow;
        await SaveAndNotifyAsync(deployment, ct);

        // Refresh nginx proxy config
        if (deployment.HostId is not null)
            await RefreshNginxConfigAsync(deployment.HostId, ct);
    }

    private async Task SaveAndNotifyAsync(LlmDeploymentEntity deployment, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateDbContext();
        var entity = await db.LlmDeployments.FindAsync([deployment.Id], ct);
        if (entity is null) return;

        entity.Status            = deployment.Status;
        entity.ErrorMessage      = deployment.ErrorMessage;
        entity.ContainerId       = deployment.ContainerId;
        entity.StartedAt         = deployment.StartedAt;
        entity.RestartCount      = deployment.RestartCount;
        entity.LastHealthCheckAt = deployment.LastHealthCheckAt;

        await db.SaveChangesAsync(ct);
        _notifier.NotifyChanged(entity);
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
