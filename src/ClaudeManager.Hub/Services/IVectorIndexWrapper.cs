using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Interface for access to the persistent vector index stored in SQLite database.
/// Provides CRUD operations for vector embeddings to ensure they survive application restarts.
/// </summary>
public interface IVectorIndexWrapper
{
    /// <summary>
    /// Loads all active vector entries from the database.
    /// Only returns entries that have a non-null embedding.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>List of loaded vector entries.</returns>
    Task<List<ViaVectorIndex>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves a new or updated vector entry to the database.
    /// If the entry with the same ID already exists, it will be updated.
    /// </summary>
    /// <param name="entry">The vector entry to save.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>Task representing the asynchronous operation.</returns>
    Task SaveAsync(ViaVectorIndex entry, CancellationToken ct = default);

    /// <summary>
    /// Deletes a vector entry from the database by its ID.
    /// </summary>
    /// <param name="id">The ID of the entry to delete.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>Task representing the asynchronous operation.</returns>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the entire vector index by generating and persisting embeddings
    /// for all active wiki entries.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>Task representing the asynchronous operation.</returns>
    Task RebuildIndexAsync(CancellationToken ct = default);
}
