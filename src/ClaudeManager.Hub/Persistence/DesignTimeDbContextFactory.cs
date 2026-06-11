using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClaudeManager.Hub.Persistence;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClaudeManagerDbContext>
{
    public ClaudeManagerDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ClaudeManagerDbContext>();
        var dbPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "claude_manager_dev.db");
        builder.UseSqlite($"Data Source={dbPath}");
        return new ClaudeManagerDbContext(builder.Options);
    }
}
