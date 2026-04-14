using System.Numerics;
using System.Text;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

public class WikiService : IWikiService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorIndexWrapper _vectorIndexWrapper;
    private readonly IKalendarSearcher _kanalaterSearcher;
    private readonly TimeSpan _fallbackTimeout;

    public WikiService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        IEmbeddingService embeddingService,
        IVectorIndexWrapper vectorIndexWrapper,
        IKalendarSearcher kanalaterSearcher)
    {
        _dbFactory = dbFactory;
        _embeddingService = embeddingService;
        _vectorIndexWrapper = vectorIndexWrapper;
        _kanalaterSearcher = kanalaterSearcher;
        _fallbackTimeout = TimeSpan.FromMilliseconds(1000);
    }

    public async Task<List<WikiEntryEntity>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Fetch first, sort client-side: SQLite does not support DateTimeOffset in ORDER BY
        var entries = await db.WikiEntries.ToListAsync();
        return entries
            .OrderBy(e => e.IsArchived)
            .ThenBy(e => e.Category)
            .ThenByDescending(e => e.UpdatedAt)
            .ToList();
    }

    public async Task<WikiEntryEntity> CreateAsync(
        string title, string category, string content, string? tags)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var entry = new WikiEntryEntity
        {
            Title      = title,
            Category   = category,
            Content    = content,
            Tags       = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim(),
            CreatedAt  = now,
            UpdatedAt  = now,
            IsArchived = false,
        };
        db.WikiEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    public async Task UpdateAsync(long id, string title, string category, string content, string? tags)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.WikiEntries.FindAsync(id);
        if (entry is null) return;
        entry.Title     = title;
        entry.Category  = category;
        entry.Content   = content;
        entry.Tags      = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim();
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task SetArchivedAsync(long id, bool archived)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.WikiEntries.FindAsync(id);
        if (entry is null) return;
        entry.IsArchived = archived;
        entry.UpdatedAt  = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(long id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entry = await db.WikiEntries.FindAsync(id);
        if (entry is null) return;
        db.WikiEntries.Remove(entry);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates a new wiki entry or updates an existing active one with the same title
    /// (case-insensitive). Called by the MCP server tool on behalf of Claude.
    /// </summary>
    public async Task UpsertByTitleAsync(string title, string category, string content, string? tags)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.WikiEntries
            .Where(e => !e.IsArchived && e.Title.ToLower() == title.ToLower())
            .FirstOrDefaultAsync();

        var now = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            existing.Category  = category;
            existing.Content   = content;
            existing.Tags      = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim();
            existing.UpdatedAt = now;
        }
        else
        {
            db.WikiEntries.Add(new WikiEntryEntity
            {
                Title      = title,
                Category   = category,
                Content    = content,
                Tags       = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim(),
                CreatedAt  = now,
                UpdatedAt  = now,
                IsArchived = false,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Formats all non-archived wiki entries as an XML context block for injection
    /// into claude prompts. Returns null if there are no active entries.
    /// </summary>
    public async Task<string?> BuildContextSummaryAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Fetch first, sort client-side: SQLite does not support DateTimeOffset in ORDER BY
        var allActive = await db.WikiEntries.Where(e => !e.IsArchived).ToListAsync();
        var entries   = allActive.OrderBy(e => e.Category).ThenByDescending(e => e.UpdatedAt).ToList();

        if (entries.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("<wiki>");

        foreach (var group in entries.GroupBy(e => e.Category))
        {
            sb.AppendLine($"  <{group.Key}>");
            foreach (var e in group)
            {
                var tagAttr = e.Tags is not null ? $" tags=\"{XmlEscape(e.Tags)}\"" : "";
                sb.AppendLine($"    <entry title=\"{XmlEscape(e.Title)}\"{tagAttr} updated=\"{e.UpdatedAt:yyyy-MM-dd}\">");
                sb.AppendLine(e.Content.TrimEnd());
                sb.AppendLine("    </entry>");
            }
            sb.AppendLine($"  </{group.Key}>");
        }

        sb.AppendLine("</wiki>");
        return sb.ToString();
    }

    /// <summary>
    /// AC-3, AC-4, AC-10: Semantic search for similar wiki entries by content similarity.
    /// Performs vector-based cosine similarity search with keyword fallback on timeout/error.
    /// </summary>
    public async Task<(List<WikiSearchHit>, float queryScore)> FindSimilarAsync(
        string query,
        int k = 5,
        CancellationToken ct = default)
    {
        try
        {
            // Load all active vectors from the database
            var vectors = await _vectorIndexWrapper.LoadAsync(ct);

            // If no vectors available, return empty results
            if (vectors.Count == 0)
                return (new List<WikiSearchHit>(), 0f);

            // Generate query embedding for semantic search
            var queryEmbedding = await _embeddingService.GenerateAsync(query, ct);

            // Get all active wiki entries for scoring
            var allEntries = await GetAllEntriesAsync(ct);

            // Compute cosine similarity between query and each vector
            var scoredResults = vectors
                .Zip(allEntries, (v, e) => new
                {
                    v: v,
                    e: e,
                    score: CosineSimilarity(queryEmbedding, v.Embedding),
                })
                // Sort by similarity score in descending order
                .OrderByDescending(item => item.score)
                // Take top k results
                .Take(k)
                .Select(item => new WikiSearchHit
                {
                    Entry = item.e,
                    Similarity = item.score,
                })
                .ToList();

            // Get the highest similarity score for the query
            var queryScore = scoredResults.Count > 0
                ? scoredResults.Max(r => r.Similarity)
                : 0f;

            return (scoredResults, queryScore);
        }
        catch
        {
            // AC-6: Fallback to keyword search on timeout or error with 1000ms threshold
            return await _kanalaterSearcher.SearchKeyword(query, k, ct);
        }
    }

    /// <summary>
    /// AC-6: Keyword-based fallback search for when vector search fails or times out.
    /// Used as a fallback mechanism for graceful degradation.
    /// </summary>
    public async Task<(List<WikiSearchHit>, float queryScore)> FindSimilarViaKeyword(
        string query,
        int k = 5,
        CancellationToken ct = default)
    {
        return await _kanalaterSearcher.SearchKeyword(query, k, ct);
    }

    /// <summary>
    /// Helper method to get all non-archived wiki entries.
    /// </summary>
    private async Task<List<WikiEntryEntity>> GetAllEntriesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WikiEntries
            .Where(e => !e.IsArchived)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Computes cosine similarity between two vectors using System.Numerics.Vector mathematics.
    /// Returns a score in range [0.0, 1.0].
    /// </summary>
    /// <summary>
    /// Computes cosine similarity between two vectors.
    /// Returns a score in range [0.0, 1.0].
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        // Validate dimensions
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length", nameof(a));

        if (a.Length == 0)
            return 0f;

        var dot = 0f;
        var normA = 0f;
        var normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        normA = (float)Math.Sqrt(normA);
        normB = (float)Math.Sqrt(normB);

        // Avoid division by zero
        if (normA == 0 || normB == 0)
            return 0f;

        var similarity = dot / (normA * normB);

        // Clamp to [0, 1] range
        return Math.Max(0f, Math.Min(1f, similarity));
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
