using System.Threading;
using System.Threading.Tasks;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Implementation of IVectorIndexWrapper - provides access to the persistent vector index stored in SQLite database.
/// Allows loading, saving, and deleting vector embeddings, plus rebuilding the entire index.
/// </summary>
public class VectorIndexWrapper : IVectorIndexWrapper
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;

    public VectorIndexWrapper(IDbContextFactory<ClaudeManagerDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Loads all vector entries from the database.
    /// Only returns entries that have a non-null embedding.
    /// </summary>
    public async Task<List<ViaVectorIndex>> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ViaVectorIndices
            .Where(v => v.Embedding != null)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Saves a new or updated vector entry to the database.
    /// If an entry with the same ID already exists, it will be updated (upsert).
    /// </summary>
    public async Task SaveAsync(ViaVectorIndex entry, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Set<ViaVectorIndex>().AddOrUpdate(entry);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a vector entry from the database by its ID.
    /// </summary>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entry = await db.ViaVectorIndices
            .FirstOrDefaultAsync(v => v.Id == id, ct);

        if (entry != null)
        {
            db.ViaVectorIndices.Remove(entry);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Rebuilds the entire vector index by:
    /// 1. Deleting all existing vectors
    /// 2. Generating embeddings for all active wiki entries
    /// 3. Persisting them to the database
    /// </summary>
    public async Task RebuildIndexAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Delete all existing vectors
        db.ViaVectorIndices.RemoveRange(db.ViaVectorIndices);

        // Generate and persist vectors for all active wiki entries
        var activeEntries = await db.WikiEntries
            .Where(e => !e.IsArchived)
            .ToListAsync(ct);

        foreach (var entry in activeEntries)
        {
            if (entry.Embedding != null && entry.Embedding.Length == 768)
            {
                var vectorIndex = new ViaVectorIndex
                {
                    Id = entry.Id,
                    SessionId = "entry-" + entry.Id,
                    Embedding = entry.Embedding,
                    CreatedAt = entry.CreatedAt,
                };

                db.ViaVectorIndices.Add(vectorIndex);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
