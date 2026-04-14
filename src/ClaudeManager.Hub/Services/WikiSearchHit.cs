using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Represents a single search hit from vector or keyword search.
/// Contains the wiki entry and its similarity score.
/// </summary>
public record WikiSearchHit
{
    /// <summary>The matched wiki entry.</summary>
    public WikiEntryEntity Entry { get; init; } = default!;

    /// <summary>
    /// Similarity score in range 0.0-1.0.
    /// For vector search: cosine similarity.
    /// For keyword search: keyword-based relevance score.
    /// </summary>
    public float Similarity { get; init; }
}
