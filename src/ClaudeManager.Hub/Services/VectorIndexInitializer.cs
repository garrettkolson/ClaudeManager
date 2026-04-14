using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ClaudeManager.Hub.Services;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// IHostedService that initializes the vector index on application startup.
/// Calls InitVectorIndexAsync on WikiService after a 2-second delay
/// to allow the embedding model to fully load.
/// </summary>
public class VectorIndexInitializer : IHostedService, IAsyncDisposable
{
    private readonly IWikiService _wikiService;
    private readonly ILogger _logger;

    public VectorIndexInitializer(
        IWikiService wikiService,
        ILogger<VectorIndexInitializer> logger)
    {
        _wikiService = wikiService;
        _logger = logger;
    }

    /// <summary>
    /// StartAsync: Wait 2 seconds for model to load, then initialize vector index.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Wait for embedding model to fully load (2 seconds)
            await Task.Delay(TimeSpan.FromMilliseconds(2000), cancellationToken);

            // Initialize vector index with embeddings for all active wiki entries
            await _wikiService.InitVectorIndexAsync(cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // Operation was cancelled
            _logger.LogInformation("VectorIndexInitializer: StartAsync cancelled, gracefully stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VectorIndexInitializer: Failed to initialize vector index");
        }
    }

    /// <summary>
    /// StopAsync: Ensure graceful shutdown.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("VectorIndexInitializer: StopAsync called");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Dispose: Ensure clean up if needed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _wikiService?.Dispose();
        GC.SuppressFinalize(this);
    }
}
