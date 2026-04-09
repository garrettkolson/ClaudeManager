using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Deployment-focused logging service that provides log retrieval and auto-refresh streaming.
/// Injected via dependency injection with IServiceProvider.
/// </summary>
public interface ILlmDeploymentLoggingService
{
    /// <summary>
    /// Returns the latest logs for the given deployment ID.
    /// </summary>
    /// <param name="deploymentId">The deployment ID to fetch logs for.</param>
    /// <param name="lines">Number of log lines to return (default: 200).</param>
    /// <param name="ct">Cancellation token for graceful cancellation.</param>
    /// <returns>Tuple of (Logs, Error) where Logs is the log text or null if an error occurred.</returns>
    Task<(string? Logs, string? Error)> GetLogsAsync(
        long deploymentId, int lines = 200, CancellationToken ct = default);

    /// <summary>
    /// Provides asynchronous log streaming with auto-refresh every 5 seconds.
    /// The stream is cancelled gracefully when the cancellation token is triggered.
    /// </summary>
    /// <param name="deploymentId">The deployment ID to fetch logs for.</param>
    /// <param name="lines">Number of log lines to fetch per refresh (default: 200).</param>
    /// <param name="ct">Cancellation token for graceful cancellation of streaming.</param>
    /// <returns>An async enumerable of log strings that refreshes every 5 seconds.</returns>
    IAsyncEnumerable<string> SubscribeAsync(
        long deploymentId, int lines = 200,
        [EnumeratorCancellation] CancellationToken ct = default);

    /// <summary>
    /// The interval for auto-refresh in SubscribeAsync (5 seconds).
    /// </summary>
    TimeSpan SubscribeRefreshInterval { get; }

    void Dispose();
}

/// <summary>
/// Deployment-focused logging service that provides log retrieval and auto-refresh streaming.
/// Injected via dependency injection with IServiceProvider.
/// </summary>
public class LlmDeploymentLoggingService : ILlmDeploymentLoggingService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LlmDeploymentLoggingService> _logger;
    private readonly object _lock = new();
    private static readonly ConcurrentDictionary<long, Channel<string>> _logCache = new();

    /// <summary>
    /// The interval for auto-refresh in SubscribeAsync (5 seconds).
    /// </summary>
    public TimeSpan SubscribeRefreshInterval { get; } = TimeSpan.FromSeconds(5);

    public LlmDeploymentLoggingService(
        IServiceProvider serviceProvider,
        ILogger<LlmDeploymentLoggingService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Opens the log cache channel for the given deployment.
    /// </summary>
    private static Channel<string> OpenChannel(long deploymentId)
    {
        if (_logCache.TryGetValue(deploymentId, out var channel))
            return channel;

        channel = Channel.CreateUnbounded<string>();
        _logCache[deploymentId] = channel;
        return channel;
    }

    /// <summary>
    /// Closes the log cache channel for the given deployment.
    /// </summary>
    private void Cleanup(long deploymentId)
    {
        lock (_lock)
        {
            if (_logCache.TryRemove(deploymentId, out var channel))
                channel.Complete();
        }
    }

    /// <summary>
    /// Returns the latest logs for the given deployment ID.
    /// </summary>
    public async Task<(string? Logs, string? Error)> GetLogsAsync(
        long deploymentId, int lines = 200, CancellationToken ct = default)
    {
        try
        {
            var deployment = await GetCurrentDeploymentAsync(deploymentId, ct);

            if (deployment is null)
                return (null, $"Deployment with ID {deploymentId} not found.");

            if (deployment.ContainerId is null)
                return (null, $"No container associated with deployment {deploymentId}.");

            var host = await GetCurrentHostAsync(deployment.HostId, ct);
            if (host is null)
                return (null, $"GPU host for deployment {deploymentId} not found.");

            var instanceService = _serviceProvider
                .GetService<LlmInstanceService>();

            if (instanceService is null)
                return (null, "LlmInstanceService not available via dependency injection.");

            var result = await instanceService.GetLogsAsync(host, deployment.ContainerId!, lines, ct);
            if (result is null || string.IsNullOrEmpty(result.Logs))
                return ("", null);

            return (result.Logs, result.Error);
        }
        catch (OperationCanceledException)
        {
            return (null, "Log retrieval was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving logs for deployment {Id}", deploymentId);
            return (null, $"Unexpected error retrieving logs: {ex.Message}");
        }
    }

    /// <summary>
    /// Provides asynchronous log streaming with auto-refresh every 5 seconds.
    /// The stream is cancelled gracefully when the cancellation token is triggered.
    /// </summary>
    public async IAsyncEnumerable<string> SubscribeAsync(
        long deploymentId, int lines = 200,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = OpenChannel(deploymentId);
        bool cacheChannelClosed = false;

        try
        {
            // Get initial logs
            var initialLogs = await GetCurrentLogsAsync(deploymentId, lines, ct);
            if (!string.IsNullOrEmpty(initialLogs))
            {
                foreach (var line in initialLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    await channel.Writer.WriteAsync(line, ct);
                }
            }

            // Wait for the 5-second refresh interval then stream subsequent logs
            while (!ct.IsCancellationRequested)
            {
                // Wait for the 5-second refresh interval
                try
                {
                    await Task.Delay(SubscribeRefreshInterval, ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                try
                {
                    var logs = await GetCurrentLogsAsync(deploymentId, lines, ct);
                    foreach (var line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            await channel.Writer.WriteAsync(line, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (ChannelClosedException)
                        {
                            if (cacheChannelClosed)
                                break;
                            break;
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error in SubscribeAsync for deployment {Id}", deploymentId);
                    yield break;
                }
            }
        }
        finally
        {
            Cleanup(deploymentId);
        }
    }

    /// <summary>
    /// Fetches logs for a deployment asynchronously.
    /// </summary>
    private async Task<string> GetCurrentLogsAsync(long deploymentId, int lines = 200, CancellationToken ct = default)
    {
        try
        {
            var (logs, error) = await GetCurrentDeploymentLogsAsync(deploymentId, lines, ct);

            if (error is not null && !error.Contains("not found"))
            {
                _logger.LogError(error, "Error fetching logs for deployment {Id}", deploymentId);
            }

            return logs ?? "";
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching logs for deployment {Id}", deploymentId);
            return "";
        }
    }

    /// <summary>
    /// Returns the deployment entity for the given ID.
    /// </summary>
    private async Task<LlmDeploymentEntity> GetCurrentDeploymentAsync(
        long deploymentId, CancellationToken ct)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider
            .GetService<ClaudeManager.Hub.Persistence.IClaudeManagerDbContext>();

        if (db is null)
            throw new InvalidOperationException("Database context not available via dependency injection.");

        return await db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
    }

    /// <summary>
    /// Returns the GPU host entity for the given hostId.
    /// </summary>
    private async Task<GpuHostEntity> GetCurrentHostAsync(
        long hostId, CancellationToken ct = default)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider
            .GetService<ClaudeManager.Hub.Persistence.IClaudeManagerDbContext>();

        if (db is null)
            throw new InvalidOperationException("Database context not available via dependency injection.");

        return await db.GpuHosts
            .FirstOrDefaultAsync(h => h.HostId == hostId, ct);
    }

    /// <summary>
    /// Returns logs for a deployment from the database and current state.
    /// </summary>
    private async Task<(string? Logs, string? Error)> GetCurrentDeploymentLogsAsync(
        long deploymentId, int lines = 200, CancellationToken ct = default)
    {
        var deployment = await GetCurrentDeploymentAsync(deploymentId, ct);

        if (deployment is null)
            return (null, $"Deployment with ID {deploymentId} not found");

        if (deployment.ContainerId is null)
            return (null, $"No container associated with deployment {deploymentId}.");

        var host = await GetCurrentHostAsync(deployment.HostId, ct);
        if (host is null)
            return (null, $"GPU host for deployment {deploymentId} not found.");

        var instanceService = _serviceProvider
            .GetService<LlmInstanceService>();

        if (instanceService is null)
            return (null, "LlmInstanceService not available via dependency injection.");

        var result = await instanceService.GetLogsAsync(host, deployment.ContainerId, lines, ct);
        return (result.Logs, result.Error);
    }

    /// <summary>
    /// Disposes the service.
    /// </summary>
    public void Dispose()
    {
        Cleanup(0);
    }
}
