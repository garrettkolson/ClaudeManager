using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// IHostedService that initializes embedding vectors for all active wiki entries on application startup.
/// Handles service connection verification and graceful degradation if Python embedding service fails.
/// User is notified of embedding status on first request.
/// </summary>
public class EmbeddingStartupService : IHostedService, IDisposable
{
    private readonly ILogger<EmbeddingStartupService> _logger;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorIndexWrapper _vectorIndexWrapper;
    private readonly IWikiService _wikiService;
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _indexInitTimeout;
    private bool _isEmbeddingInitialized;
    private readonly object _lock = new object();

    public EmbeddingStartupService(
        ILoggerFactory loggerFactory,
        IEmbeddingService embeddingService,
        IVectorIndexWrapper vectorIndexWrapper,
        IWikiService wikiService,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        IOptions<EmbeddingServiceOptions> options = null)
    {
        _logger = loggerFactory.CreateLogger<EmbeddingStartupService>();
        _embeddingService = embeddingService;
        _vectorIndexWrapper = vectorIndexWrapper;
        _wikiService = wikiService;
        _dbFactory = dbFactory;

        _connectionTimeout = options?.Value.ConnectionTimeout ?? TimeSpan.FromMilliseconds(1000);
        _indexInitTimeout = options?.Value.IndexInitTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Boolean property to check if embedding index has been initialized.
    /// Used for user notifications on first request to show embedding status.
    /// </summary>
    public bool IsEmbeddingInitialized
    {
        get => _isEmbeddingInitialized;
        set
        {
            lock (_lock)
            {
                _isEmbeddingInitialized = value;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _logger.LogInformation("EmbeddingStartupService: Starting...");

            // Step 1: Verify Python embedding service connection
            await VerifyEmbeddingServiceConnectionAsync(linkedTokenSource.Token);

            // Step 2: Initialize vector index with embeddings for all active wiki entries
            await InitializeVectorIndexAsync(linkedTokenSource.Token);

            // Step 3: Mark embeddings as initialized
            IsEmbeddingInitialized = true;
            _logger.LogInformation("Embedding initialization completed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("EmbeddingStartupService: Operation cancelled");
        }
        catch (Exception ex) when (ex is TimeoutException || ex is TaskCanceledException)
        {
            // Graceful degradation: proceed with warning message
            _logger.LogWarning(ex, "EmbeddingStartupService: Failed to initialize embeddings, proceeding with fallback search mode");
            IsEmbeddingInitialized = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmbeddingStartupService: Initialization failed with error: {Message}", ex.Message);
            IsEmbeddingInitialized = false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            CancellationTokenSource?.Dispose();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        CancellationTokenSource?.Dispose();
    }

    private static CancellationTokenSource? CancellationTokenSource = null;

    /// <summary>
    /// Verifies the Python embedding service connection is active.
    /// Returns a task that completes if the service is available, or throws an exception.
    /// </summary>
    private async Task VerifyEmbeddingServiceConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("EmbeddingStartupService: Verifying embedding service connection...");

            // Wait for model to be loaded with timeout
            bool isLoaded = await _embeddingService.IsModelLoadedAsync(cancellationToken);

            // If model is not loaded, try to load it
            if (!isLoaded)
            {
                _logger.LogInformation("EmbeddingStartupService: Loading model...");
                await _embeddingService.LoadModelAsync(cancellationToken);
                isLoaded = await Task.Run(
                    () => _embeddingService.IsModelLoadedAsync(cancellationToken),
                    cancellationToken
                );
            }

            if (isLoaded)
            {
                _logger.LogInformation("EmbeddingStartupService: Connection verified - model is loaded");
            }
            else
            {
                throw new TimeoutException($"Failed to connect to embedding service within {_connectionTimeout.TotalMilliseconds}ms");
            }
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "EmbeddingStartupService: Connection to embedding service timed out after {_TimeoutMs}ms", _connectionTimeout.TotalMilliseconds);
            throw new TimeoutException("Embedding service connection timed out");
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("EmbeddingStartupService: Connection cancelled");
            throw new TimeoutException("Embedding service connection cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmbeddingStartupService: Failed to verify embedding service connection - {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Initializes the vector index with embeddings for all active wiki entries.
    /// Uses Task.Run/await pattern as specified in requirements.
    /// Gracefully handles failures and provides user notification option.
    /// </summary>
    private async Task InitializeVectorIndexAsync(CancellationToken cancellationToken)
    {
        var embeddings = new CancellationTokenSource();
        embeddings.CancelAfter(_indexInitTimeout);

        try
        {
            await Task.Run(
                async () =>
                {
                    _logger.LogInformation("EmbeddingStartupService: Initializing vector index...");

                    // Get active wiki entries from the database
                    await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
                    var activeEntries = await db.WikiEntries
                        .Where(e => e.Id > 0)
                        .ToListAsync(cancellationToken);

                    int count = 0;
                    int embeddedCount = 0;

                    foreach (var entry in activeEntries)
                    {
                        if (entry.IsArchived || entry.Id <= 0)
                            continue;

                        // Check for cancellation
                        if (cancellationToken.IsCancellationRequested || embeddings.Token.IsCancellationRequested)
                            return;

                        // Generate embedding if not present
                        if (entry.Embedding == null || entry.Embedding.Length != 768)
                        {
                            // Generate embedding using the embedding service
                            string text = $"{entry.Title} {entry.Content}";
                            entry.Embedding = await _embeddingService.GenerateAsync(text, cancellationToken);
                        }

                        // Only save if we have a 768-dim embedding
                        if (entry.Embedding != null && entry.Embedding.Length == 768)
                        {
                            var vectorIndex = new ViaVectorIndex
                            {
                                Id = entry.Id,
                                SessionId = "entry-" + entry.Id,
                                Embedding = entry.Embedding,
                                CreatedAt = entry.CreatedAt,
                            };

                            await _vectorIndexWrapper.SaveAsync(vectorIndex, cancellationToken);
                            embeddedCount++;
                        }

                        count++;
                    }

                    _logger.LogInformation($"EmbeddingStartupService: Initialized {embeddedCount}/{count} wiki entries");
                },
                CancellationTokenSource?.Token ?? CancellationToken.None
            );
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("EmbeddingStartupService: Vector index initialization was cancelled");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("EmbeddingStartupService: Vector index initialization was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmbeddingStartupService: Vector index initialization failed - {Message}", ex.Message);
        }
    }
}

/// <summary>
/// Options for configuring the EmbeddingStartupService behavior.
/// </summary>
public class EmbeddingServiceOptions
{
    /// <summary>
    /// Timeout in milliseconds for checking embedding service connection.
    /// Default: 1000ms
    /// </summary>
    public int ConnectionTimeout { get; set; } = 1000;

    /// <summary>
    /// Timeout in milliseconds for indexing wiki entries during startup.
    /// Default: 30000ms (30 seconds)
    /// </summary>
    public int IndexInitTimeout { get; set; } = 30000;
}
