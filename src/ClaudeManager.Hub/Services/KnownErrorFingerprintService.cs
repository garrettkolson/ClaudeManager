using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

public class KnownErrorFingerprintService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;

    public KnownErrorFingerprintService(IDbContextFactory<ClaudeManagerDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public static string ComputeFingerprint(string errorMessage)
    {
        var normalized = string.Join(" ",
            errorMessage.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash);
    }

    public async Task<KnownErrorEntity?> FindByFingerprintAsync(string fingerprint, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.KnownErrors
            .FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, ct);
    }

    public async Task<KnownErrorEntity> UpsertAsync(
        string fingerprint, string description, KnownErrorStatus status,
        string? jiraKey = null, string? metadataJson = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.KnownErrors.FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, ct);

        if (existing is null)
        {
            var entity = new KnownErrorEntity
            {
                Fingerprint   = fingerprint,
                Description   = description,
                Status        = status,
                JiraKey       = jiraKey,
                MetadataJson  = metadataJson,
                FirstSeenAt   = DateTimeOffset.UtcNow,
            };
            db.KnownErrors.Add(entity);
            await db.SaveChangesAsync(ct);
            return entity;
        }
        else
        {
            existing.Description   = description;
            existing.Status        = status;
            existing.JiraKey       = jiraKey;
            existing.MetadataJson  = metadataJson;
            existing.ResolvedAt    = status is KnownErrorStatus.Fixed or KnownErrorStatus.Deferred
                ? DateTimeOffset.UtcNow : null;
            await db.SaveChangesAsync(ct);
            return existing;
        }
    }

    public async Task MarkFixedAsync(string fingerprint, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KnownErrors.FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, ct);
        if (entity is not null)
        {
            entity.Status = KnownErrorStatus.Fixed;
            entity.ResolvedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task MarkDeferredAsync(string fingerprint, DateTimeOffset after, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KnownErrors.FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, ct);
        if (entity is not null)
        {
            entity.Status = KnownErrorStatus.Deferred;
            entity.NextTriggerAfter = after;
            entity.ResolvedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task IncrementTriggerCountAsync(string fingerprint, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KnownErrors.FirstOrDefaultAsync(e => e.Fingerprint == fingerprint, ct);
        if (entity is not null)
        {
            entity.TriggerCount++;
            await db.SaveChangesAsync(ct);
        }
    }
}
