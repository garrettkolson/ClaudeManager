using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>CRUD operations for registered GPU hosts.</summary>
public class GpuHostService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;

    public GpuHostService(IDbContextFactory<ClaudeManagerDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<GpuHostEntity>> GetAllAsync()
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.GpuHosts.OrderBy(h => h.DisplayName).ToListAsync();
    }

    public async Task<GpuHostEntity?> GetByIdAsync(long id)
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.GpuHosts.FindAsync(id);
    }

    public async Task<GpuHostEntity?> GetByHostIdAsync(string hostId)
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.GpuHosts.FirstOrDefaultAsync(h => h.HostId == hostId);
    }

    public async Task<GpuHostEntity> AddAsync(GpuHostEntity host)
    {
        host.AddedAt = DateTimeOffset.UtcNow;
        host.HostId  = string.IsNullOrWhiteSpace(host.HostId)
            ? Guid.NewGuid().ToString("N")[..12]
            : host.HostId;

        await using var db = _dbFactory.CreateDbContext();
        db.GpuHosts.Add(host);
        await db.SaveChangesAsync();
        return host;
    }

    public async Task UpdateAsync(GpuHostEntity host)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.GpuHosts.Update(host);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(long id)
    {
        await using var db = _dbFactory.CreateDbContext();
        var host = await db.GpuHosts.FindAsync(id);
        if (host is not null)
        {
            db.GpuHosts.Remove(host);
            await db.SaveChangesAsync();
        }
    }
}
