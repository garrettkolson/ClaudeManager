using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Orchestrates vLLM deployment lifecycle: CRUD, start/stop, and log retrieval.
/// </summary>
public class LlmDeploymentService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly LlmInstanceService     _instance;
    private readonly GpuHostService         _gpuHosts;
    private readonly HubSecretService       _secrets;
    private readonly LlmDeploymentNotifier  _notifier;
    private readonly ILogger<LlmDeploymentService> _logger;

    public LlmDeploymentService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        LlmInstanceService    instance,
        GpuHostService        gpuHosts,
        HubSecretService      secrets,
        LlmDeploymentNotifier notifier,
        ILogger<LlmDeploymentService> logger)
    {
        _dbFactory = dbFactory;
        _instance  = instance;
        _gpuHosts  = gpuHosts;
        _secrets   = secrets;
        _notifier  = notifier;
        _logger    = logger;
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
        string quantization = "none", string? extraArgs = null, string? hfTokenOverride = null)
    {
        var entity = new LlmDeploymentEntity
        {
            DeploymentId   = Guid.NewGuid().ToString("N")[..12],
            HostId         = hostId,
            ModelId        = modelId.Trim(),
            GpuIndices     = gpuIndices.Trim(),
            HostPort       = hostPort,
            Quantization   = quantization,
            ExtraArgs      = string.IsNullOrWhiteSpace(extraArgs) ? null : extraArgs.Trim(),
            HfTokenOverride = string.IsNullOrWhiteSpace(hfTokenOverride) ? null : hfTokenOverride.Trim(),
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

        // Transition to Starting
        await SetStatusAsync(deployment, LlmDeploymentStatus.Starting, containerId: null, error: null);

        var (containerId, error) = await _instance.StartContainerAsync(host, deployment, hfToken, ct);

        if (error is not null)
        {
            await SetStatusAsync(deployment, LlmDeploymentStatus.Error, containerId: null, error: error);
            return error;
        }

        deployment.ContainerId = containerId;
        deployment.StartedAt   = DateTimeOffset.UtcNow;
        await SetStatusAsync(deployment, LlmDeploymentStatus.Running, containerId, error: null);
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
            await SetStatusAsync(deployment, LlmDeploymentStatus.Stopped, containerId: null, error: null);
            return null;
        }

        var host = await _gpuHosts.GetByHostIdAsync(deployment.HostId);
        if (host is null) return $"GPU host '{deployment.HostId}' not found.";

        var error = await _instance.StopContainerAsync(host, deployment.ContainerId, ct);

        await SetStatusAsync(deployment, LlmDeploymentStatus.Stopped,
            containerId: null,
            error: error is not null ? $"Stop warning: {error}" : null);

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
        LlmDeploymentStatus status, string? containerId, string? error)
    {
        await using var db = _dbFactory.CreateDbContext();
        var entity = await db.LlmDeployments.FindAsync(deployment.Id);
        if (entity is null) return;

        entity.Status      = status;
        entity.ErrorMessage = error;
        if (containerId is not null) entity.ContainerId = containerId;
        if (status == LlmDeploymentStatus.Stopped) entity.ContainerId = null;

        await db.SaveChangesAsync();

        // Reflect changes back to the in-memory reference for the caller
        deployment.Status       = entity.Status;
        deployment.ErrorMessage = entity.ErrorMessage;
        deployment.ContainerId  = entity.ContainerId;

        _notifier.NotifyChanged(entity);
    }
}
