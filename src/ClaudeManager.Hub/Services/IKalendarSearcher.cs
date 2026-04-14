using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Keyword-based fallback search for when vector search fails or times out.
/// Returns results ranked by keyword similarity score.
/// </summary>
public interface IKalendarSearcher
{
    /// <summary>
    /// Performs keyword-based search and returns results ordered by similarity score.
    /// The similarity score is in range 0.0-1.0.
    /// </summary>
    /// <param name="query">The search query string</param>
    /// <param name="k">Maximum number of results to return (default 5)</param>
    /// <param name="ct">Cancellation token for async operations</param>
    /// <returns>A tuple containing the list of search hits and the maximum query score</returns>
    Task<(List<WikiSearchHit>, float queryScore)> SearchKeyword(
        string query,
        int k = 5,
        CancellationToken ct = default);
}
