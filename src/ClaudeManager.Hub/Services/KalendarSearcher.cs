using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Implementation of IKalendarSearcher for keyword-based fallback search.
/// Stores wiki entries in memory for fast keyword lookup.
/// </summary>
public class KalendarSearcher : IKalendarSearcher
{
    private List<WikiEntryEntity> _entries = new();

    /// <summary>
    /// Configurable timeout threshold in milliseconds for fallback behavior.
    /// Default is 1000ms as per AC-6 requirement.
    /// </summary>
    public TimeSpan TimeoutThreshold { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Stop words to filter out from search results (case-insensitive).
    /// </summary>
    private static readonly HashSet<string> _stopWords = new(
        new[] { "a", "an", "the", "is", "are", "was", "were", "in", "on", "at", "to", "of" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of KalendarSearcher.
    /// Loads all active wiki entries into memory.
    /// </summary>
    /// <param name="entries">Collection of wiki entries to index</param>
    public KalendarSearcher(List<WikiEntryEntity> entries = null)
    {
        _entries = entries ?? new List<WikiEntryEntity>();
    }

    /// <summary>
    /// Adds entries to the indexer.
    /// </summary>
    public void AddRange(List<WikiEntryEntity> entries)
    {
        _entries.AddRange(entries);
    }

    /// <summary>
    /// Clears all entries from the indexer.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
    }

    /// <summary>
    /// Performs keyword-based search and returns results ordered by similarity score.
    /// The similarity score is in range 0.0-1.0.
    /// </summary>
    /// <param name="query">The search query string</param>
    /// <param name="k">Maximum number of results to return (default 5)</param>
    /// <param name="ct">Cancellation token for async operations</param>
    /// <returns>A tuple containing the list of search hits and the maximum query score</returns>
    public async Task<(List<WikiSearchHit>, float queryScore)> SearchKeyword(
        string query,
        int k = 5,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            // Tokenize the query, removing stop words
            var tokens = GetTokens(query);

            if (tokens.Length == 0)
            {
                return (new List<WikiSearchHit>(), 0f);
            }

            // Filter only active entries and score each one
            var scored = _entries
                .Where(e => !e.IsArchived)
                .Select(e => new
                {
                    Entry = e,
                    Score = CalculateKeywordScore(e, tokens)
                })
                .OrderByDescending(x => x.Score)
                .Take(k)
                .ToList();

            // Calculate query score based on how many tokens can be matched proportionally
            var topScores = scored.Select(s => s.Score).ToList();
            float queryScore = CalculateQueryScore(topScores, tokens.Length);

            return (
                scored.Select(s => new WikiSearchHit
                {
                    Entry = s.Entry,
                    Similarity = s.Score
                }).ToList(),
                queryScore
            );
        }, ct);
    }

    /// <summary>
    /// Calculates a keyword-based relevance score for a wiki entry.
    /// Returns a score in range 0.0-1.0.
    /// </summary>
    private float CalculateKeywordScore(WikiEntryEntity entry, string[] tokens)
    {
        var entryText = (entry.Title + " " + entry.Content + " " + (entry.Tags ?? ""))
            .ToLowerInvariant();

        var occurrences = tokens
            .Where(t => !string.IsNullOrEmpty(t))
            .ToDictionary(t => t, t => 0);

        foreach (var token in tokens)
        {
            if (token == null) continue;
            var count = CountTokenOccurrences(entryText, token);
            if (count > 0)
            {
                occurrences[token] = count;
            }
        }

        // Calculate score as average token match density
        var allMatches = occurrences.Values.Sum();
        var tokenLengthSum = tokens.Where(t => !string.IsNullOrEmpty(t)).Sum(t => t.Length);

        return tokenLengthSum > 0
            ? (float)(allMatches / (double)tokenLengthSum)
            : 0f;
    }

    /// <summary>
    /// Counts how many times a token appears in the text (case insensitive).
    /// </summary>
    private int CountTokenOccurrences(string text, string token)
    {
        return text.Split(token, StringSplitOptions.None).Length - 1;
    }

    /// <summary>
    /// Generates tokens from a query string, removing stop words and normalizing.
    /// </summary>
    private string[] GetTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = query.ToLowerInvariant().Trim();
        var tokens = normalized
            .Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0 && !_stopWords.Contains(t))
            .ToArray();

        return tokens;
    }

    /// <summary>
    /// Calculates an overall query score based on top result scores and token count.
    /// </summary>
    private float CalculateQueryScore(List<float> topScores, int tokenCount)
    {
        if (topScores.Count == 0 || tokenCount == 0)
            return 0f;

        // Average of top scores, normalized by token count
        var avgScore = topScores.Average();
        var tokenRatio = (float)Math.Min(1.0, (double)avgScore / tokenCount);
        return avgScore * tokenRatio;
    }
}
