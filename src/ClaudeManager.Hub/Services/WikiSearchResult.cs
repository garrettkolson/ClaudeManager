using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Result of a semantic search query. Contains ranked results with similarity scores.
/// </summary>
public record WikiSearchResult
{
    /// <summary>
    /// Ranked list of similar wiki entries, sorted by similarity score in descending order.
    /// Scores are in range 0.0-1.0 representing cosine similarity.
    /// </summary>
    public List<WikiSearchHit> Results { get; init; } = new();

    /// <summary>
    /// The query embedding used for this search. Serialized for debugging and tracing.
    /// This is a 768-dimensional vector used in cosine similarity computation.
    /// </summary>
    public float[] QueryEmbedding { get; init; } = Array.Empty<float>();

    /// <summary>
    /// The search method used ("semantic" or "keyword").
    /// "semantic" indicates vector-based search, "keyword" indicates fallback keyword search.
    /// </summary>
    public string SearchMethod { get; init; } = "semantic";

    /// <summary>
    /// The highest similarity score among the results. Used to characterize the overall search quality.
    /// </summary>
    public float? MatchingScore { get; init; }
}
