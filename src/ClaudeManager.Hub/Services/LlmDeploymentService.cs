using System.Text.RegularExpressions;
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

    public async Task UpdateAsync(LlmDeploymentEntity deployment)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.LlmDeployments.Update(deployment);
        await db.SaveChangesAsync();
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

        // Transition to Starting
        await SetStatusAsync(deployment, LlmDeploymentStatus.Starting, containerId: null, error: null);

        var (containerId, error) = await _instance.StartContainerAsync(host, deployment, hfToken, ct);

        if (error is not null)
        {
            await SetStatusAsync(deployment, LlmDeploymentStatus.Error, containerId: null, error: error, ct);
            return error;
        }

        // Persist ContainerId immediately so logs are accessible while the model loads.
        // Large models (35B+) can take 5-10+ minutes to initialize; do NOT block on health here.
        deployment.ContainerId = containerId;
        deployment.RestartCount = 0;
        await SetStatusAsync(deployment, LlmDeploymentStatus.Starting, containerId, error: null, ct);

        // Short fast-fail poll (30s): catches immediate startup failures (bad image, port conflict,
        // OOM on first GPU alloc, etc.) without blocking for large model loads.
        // LlmDeploymentHealthService handles the Starting → Running transition for slow loaders.
        _logger.LogInformation("Deployment {Id} container started ({ContainerId}); polling for fast failures...",
            deployment.Id, containerId![..Math.Min(12, containerId!.Length)]);

        using var fastFailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fastFailCts.CancelAfter(TimeSpan.FromSeconds(30));

        // fastFailCts.Token controls the loop window only (while condition + Delay).
        // Individual SSH operations use the outer ct so the fast-fail timeout expiring mid-command
        // doesn't cause a cancelled SSH result to be misread as "container not found".
        while (!fastFailCts.Token.IsCancellationRequested)
        {
            var isHealthy = await _instance.CheckHealthAsync(host, deployment.HostPort, ct);
            if (isHealthy)
            {
                _logger.LogInformation("Deployment {Id} is healthy (fast startup)", deployment.Id);
                deployment.StartedAt         = DateTimeOffset.UtcNow;
                deployment.LastHealthCheckAt = DateTimeOffset.UtcNow;
                await SetStatusAsync(deployment, LlmDeploymentStatus.Running, containerId, error: null, ct);
                return null;
            }

            // Not healthy yet — check that the container hasn't already exited.
            // Only do this if the fast-fail window hasn't expired; otherwise we're done anyway.
            if (fastFailCts.Token.IsCancellationRequested) break;

            var containerStatus = await _instance.InspectContainerAsync(host, containerId, ct);
            if (containerStatus is null || containerStatus != ContainerStatus.Running)
            {
                var msg = $"Container exited immediately after start (status: {containerStatus?.ToString() ?? "not found"})";
                _logger.LogWarning("Deployment {Id}: {Message}", deployment.Id, msg);
                await SetStatusAsync(deployment, LlmDeploymentStatus.Error, containerId: null, error: msg, ct);
                return msg;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), fastFailCts.Token); }
            catch (OperationCanceledException) { break; }
        }

        // Container is running but vLLM hasn't finished loading within 30s — normal for large models.
        // Leave in Starting; LlmDeploymentHealthService will poll and transition to Running.
        _logger.LogInformation(
            "Deployment {Id} model still loading; background health service will track progress.",
            deployment.Id);
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

    // ── Container detection ───────────────────────────────────────────────────

    /// <summary>
    /// Scans the given host for running vLLM containers and correlates them with
    /// deployments already tracked in the database.
    /// </summary>
    public async Task<(List<DetectedVllmContainer> Containers, string? Error)> DetectContainersAsync(
        GpuHostEntity host, CancellationToken ct = default)
    {
        var (rawList, error) = await _instance.ListRunningVllmContainersAsync(host, ct);
        if (error is not null) return ([], error);

        await using var db = _dbFactory.CreateDbContext();
        var existing = await db.LlmDeployments
            .Where(d => d.HostId == host.HostId)
            .ToListAsync(ct);

        var results = rawList.Select(raw =>
        {
            var match = existing.FirstOrDefault(d => d.ContainerId == raw.ContainerId);
            return new DetectedVllmContainer(
                ContainerId:            raw.ContainerId,
                ImageTag:               raw.ImageTag,
                ModelId:                raw.ModelId,
                HostPort:               raw.HostPort,
                GpuIndices:             raw.GpuIndices,
                Quantization:           raw.Quantization,
                MaxModelLen:            raw.MaxModelLen,
                AlreadyTracked:         match is not null,
                ExistingDeploymentDbId: match?.Id,
                ExistingDeploymentId:   match?.DeploymentId);
        }).ToList();

        return (results, null);
    }

    /// <summary>
    /// Creates a new deployment record for a container that was detected but not yet tracked.
    /// The deployment is created in Running status with the container ID already associated.
    /// </summary>
    public async Task<(LlmDeploymentEntity Deployment, string? Error)> ImportDetectedAsync(
        string hostId, DetectedVllmContainer detected, CancellationToken ct = default)
    {
        if (detected.AlreadyTracked)
            return (null!, "Container is already tracked by an existing deployment.");

        // Build a short readable slug from the model name
        var rawName = string.IsNullOrWhiteSpace(detected.ModelId) ? "vllm" :
            detected.ModelId.Contains('/')
                ? detected.ModelId[(detected.ModelId.LastIndexOf('/') + 1)..]
                : detected.ModelId;
        var slug     = Regex.Replace(rawName, @"[^a-zA-Z0-9]", "-").ToLowerInvariant().Trim('-');
        var prefix   = slug.Length > 7 ? slug[..7] : slug;
        var deployId = $"{prefix}-{Guid.NewGuid().ToString("N")[..4]}";

        var entity = new LlmDeploymentEntity
        {
            DeploymentId  = deployId,
            HostId        = hostId,
            ModelId       = detected.ModelId ?? "(unknown)",
            GpuIndices    = detected.GpuIndices ?? "0",
            HostPort      = detected.HostPort ?? 8000,
            Quantization  = detected.Quantization ?? "none",
            ImageTag      = detected.ImageTag,
            MaxModelLen   = detected.MaxModelLen,
            ContainerId   = detected.ContainerId,
            Status        = LlmDeploymentStatus.Running,
            StartedAt     = DateTimeOffset.UtcNow,
            CreatedAt     = DateTimeOffset.UtcNow,
        };

        await using var db = _dbFactory.CreateDbContext();
        db.LlmDeployments.Add(entity);
        await db.SaveChangesAsync(ct);

        _notifier.NotifyChanged(entity);
        return (entity, null);
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
        if (containerId is not null)
            entity.ContainerId = containerId;
        else if (status is LlmDeploymentStatus.Stopped or LlmDeploymentStatus.Error)
            entity.ContainerId = null;

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
