using System.Text;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

public class WikiService : IWikiService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;

    public WikiService(IDbContextFactory<ClaudeManagerDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<WikiEntryEntity>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WikiEntries
            .OrderBy(e => e.IsArchived)
            .ThenBy(e => e.Category)
            .ThenByDescending(e => e.UpdatedAt)
            .ToListAsync();
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
        var entries = await db.WikiEntries
            .Where(e => !e.IsArchived)
            .OrderBy(e => e.Category)
            .ThenByDescending(e => e.UpdatedAt)
            .ToListAsync();

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

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
