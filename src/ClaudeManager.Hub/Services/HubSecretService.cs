using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Key/value store for Hub-wide secrets (e.g. HuggingFace token).
/// Values are persisted in the HubSecrets table.
/// </summary>
public class HubSecretService
{
    public const string HuggingFaceTokenKey = "HuggingFaceToken";

    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;

    public HubSecretService(IDbContextFactory<ClaudeManagerDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<string?> GetAsync(string key)
    {
        await using var db = _dbFactory.CreateDbContext();
        var entity = await db.HubSecrets.FindAsync(key);
        return entity?.Value;
    }

    public async Task SetAsync(string key, string? value)
    {
        await using var db = _dbFactory.CreateDbContext();
        var entity = await db.HubSecrets.FindAsync(key);
        if (entity is null)
        {
            db.HubSecrets.Add(new HubSecretEntity { Key = key, Value = value });
        }
        else
        {
            entity.Value = value;
            db.HubSecrets.Update(entity);
        }
        await db.SaveChangesAsync();
    }
}
