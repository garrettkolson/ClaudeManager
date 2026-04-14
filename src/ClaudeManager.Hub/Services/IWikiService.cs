namespace ClaudeManager.Hub.Services;

public interface IWikiService
{
    /// <summary>
    /// Builds a context summary from all non-archived wiki entries for agent context.
    /// </summary>
    Task<string?> BuildContextSummaryAsync();

    /// <summary>
    /// AC-3, AC-4, AC-10: Semantic search for similar wiki entries by content similarity.
    /// Performs vector-based cosine similarity search with keyword fallback on timeout/error.
    /// </summary>
    /// <param name="query">The natural language search query.</param>
    /// <param name="k">Number of top results to return (default 5).</param>
    /// <param name="ct">Cancellation token for async operations.</param>
    /// <returns>Tuple of (results, queryScore) where queryScore is the highest similarity in results.</returns>
    Task<(List<WikiSearchHit> Results, float queryScore)> FindSimilarAsync(
        string query,
        int k = 5,
        CancellationToken ct = default);

    /// <summary>
    /// AC-6: Keyword-based fallback search for when vector search fails or times out.
    /// Used as a fallback mechanism for graceful degradation.
    /// </summary>
    /// <param name="query">The query text for keyword matching.</param>
    /// <param name="k">Number of top results to return (default 5).</param>
    /// <param name="ct">Cancellation token for async operations.</param>
    /// <returns>Tuple of (results, queryScore) where queryScore is the highest similarity in results.</returns>
    Task<(List<WikiSearchHit> Results, float queryScore)> FindSimilarViaKeyword(
        string query,
        int k = 5,
        CancellationToken ct = default);
}
