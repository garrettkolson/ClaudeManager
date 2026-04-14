namespace ClaudeManager.Hub.Services;

/// <summary>
/// Generates semantic embeddings using sentence-transformers all-mpnet-base-v2 model.
/// Supports timeout during inference and model warm-up.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Generate a sentence embedding for the given text (768-dim float[]).</summary>
    /// <param name="text">The input text to embed</param>
    /// <param name="ct">Cancellation token for async operations</param>
    /// <returns>768-dimensional float array embedding</returns>
    Task<float[]> GenerateAsync(string text, CancellationToken ct = default);

    /// <summary>Check if the embedding model is loaded and ready.</summary>
    /// <param name="ct">Cancellation token for async operations</param>
    /// <returns>True if model is loaded and ready for use</returns>
    Task<bool> IsModelLoadedAsync(CancellationToken ct = default);

    /// <summary>Load the all-mpnet-base-v2 model into memory.</summary>
    /// <param name="ct">Cancellation token for async operations</param>
    /// <returns>Task representing the load operation</returns>
    Task LoadModelAsync(CancellationToken ct = default);

    /// <summary>Timeout for embedding generation, default 1000ms.</summary>
    TimeSpan EmbeddingTimeout { get; set; } = TimeSpan.FromMilliseconds(1000);
}
