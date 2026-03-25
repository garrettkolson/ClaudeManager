using ClaudeManager.Hub.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Tests.Helpers;

/// <summary>
/// Creates an in-memory SQLite database for use in unit tests.
/// The returned SqliteConnection must be disposed by the caller (usually in TearDown).
/// </summary>
public static class InMemoryDbHelper
{
    /// <summary>
    /// Schema created via EnsureCreated — fast; suitable for service-level tests
    /// that don't care about migration history.
    /// </summary>
    public static async Task<(IDbContextFactory<ClaudeManagerDbContext> Factory, SqliteConnection Connection)>
        CreateAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = BuildOptions(conn);
        await using var db = new ClaudeManagerDbContext(opts);
        await db.Database.EnsureCreatedAsync();
        return (new TestDbContextFactory(opts), conn);
    }

    /// <summary>
    /// Schema created via MigrateAsync — required when the code under test also calls
    /// MigrateAsync (so the __EFMigrationsHistory table must already exist).
    /// </summary>
    public static async Task<(IDbContextFactory<ClaudeManagerDbContext> Factory, SqliteConnection Connection)>
        CreateWithMigrationsAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = BuildOptions(conn);
        await using var db = new ClaudeManagerDbContext(opts);
        await db.Database.MigrateAsync();
        return (new TestDbContextFactory(opts), conn);
    }

    private static DbContextOptions<ClaudeManagerDbContext> BuildOptions(SqliteConnection conn) =>
        new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(conn)
            .Options;
}

public class TestDbContextFactory(DbContextOptions<ClaudeManagerDbContext> opts)
    : IDbContextFactory<ClaudeManagerDbContext>
{
    public ClaudeManagerDbContext CreateDbContext() => new(opts);

    public Task<ClaudeManagerDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
        Task.FromResult(new ClaudeManagerDbContext(opts));
}
