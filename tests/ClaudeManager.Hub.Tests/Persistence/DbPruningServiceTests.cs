using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Tests.Helpers;
using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Persistence;

[TestFixture]
public class DbPruningServiceTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _factory = default!;

    [SetUp]
    public async Task SetUp()
    {
        (_factory, _conn) = await InMemoryDbHelper.CreateAsync();
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private DbPruningService BuildService() =>
        new(_factory, NullLogger<DbPruningService>.Instance);

    // Seeds a machine agent + session, returning the session ID.
    private async Task<string> SeedSessionAsync(
        DateTimeOffset lastActivityAt,
        string? machineId  = null,
        string? sessionId  = null,
        SessionStatus status = SessionStatus.Ended)
    {
        machineId ??= $"machine-{Guid.NewGuid():N}";
        sessionId ??= $"sess-{Guid.NewGuid():N}";

        await using var db = _factory.CreateDbContext();

        db.MachineAgents.Add(new MachineAgentEntity
        {
            MachineId        = machineId,
            DisplayName      = "Test Machine",
            Platform         = "linux",
            FirstConnectedAt = DateTimeOffset.UtcNow,
            LastConnectedAt  = DateTimeOffset.UtcNow,
            LastSeenAt       = DateTimeOffset.UtcNow,
        });

        db.ClaudeSessions.Add(new ClaudeSessionEntity
        {
            SessionId        = sessionId,
            MachineId        = machineId,
            WorkingDirectory = "/test",
            StartedAt        = lastActivityAt.AddMinutes(-5),
            LastActivityAt   = lastActivityAt,
            Status           = status,
        });

        await db.SaveChangesAsync();
        return sessionId;
    }

    private async Task SeedLineAsync(string sessionId)
    {
        await using var db = _factory.CreateDbContext();
        db.StreamedLines.Add(new StreamedLineEntity
        {
            SessionId  = sessionId,
            Timestamp  = DateTimeOffset.UtcNow,
            Kind       = StreamLineKind.AssistantToken,
            Content    = "test line",
        });
        await db.SaveChangesAsync();
    }

    // ── Retention boundary ────────────────────────────────────────────────────

    [Test]
    public async Task PruneAsync_StaleSession_IsDeleted()
    {
        var staleTime = DateTimeOffset.UtcNow.AddDays(-(DbPruningService.RetentionDays + 1));
        var sessionId = await SeedSessionAsync(staleTime);

        var svc = BuildService();
        await svc.PruneAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var exists = await db.ClaudeSessions.AnyAsync(s => s.SessionId == sessionId);
        exists.Should().BeFalse();
    }

    [Test]
    public async Task PruneAsync_RecentSession_IsKept()
    {
        var recentTime = DateTimeOffset.UtcNow.AddDays(-(DbPruningService.RetentionDays - 1));
        var sessionId  = await SeedSessionAsync(recentTime);

        var svc = BuildService();
        await svc.PruneAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var exists = await db.ClaudeSessions.AnyAsync(s => s.SessionId == sessionId);
        exists.Should().BeTrue();
    }

    // ── Cascade delete ────────────────────────────────────────────────────────

    [Test]
    public async Task PruneAsync_StaleSession_CascadeDeletesOutputLines()
    {
        var staleTime = DateTimeOffset.UtcNow.AddDays(-(DbPruningService.RetentionDays + 1));
        var sessionId = await SeedSessionAsync(staleTime);
        await SeedLineAsync(sessionId);

        var svc = BuildService();
        await svc.PruneAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var lineCount = await db.StreamedLines.CountAsync(l => l.SessionId == sessionId);
        lineCount.Should().Be(0);
    }

    [Test]
    public async Task PruneAsync_RecentSession_LinesAreKept()
    {
        var recentTime = DateTimeOffset.UtcNow.AddDays(-(DbPruningService.RetentionDays - 1));
        var sessionId  = await SeedSessionAsync(recentTime);
        await SeedLineAsync(sessionId);

        var svc = BuildService();
        await svc.PruneAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var lineCount = await db.StreamedLines.CountAsync(l => l.SessionId == sessionId);
        lineCount.Should().Be(1);
    }

    // ── Mixed stale + recent ──────────────────────────────────────────────────

    [Test]
    public async Task PruneAsync_MixedSessions_OnlyDeletesStale()
    {
        var staleTime  = DateTimeOffset.UtcNow.AddDays(-(DbPruningService.RetentionDays + 5));
        var recentTime = DateTimeOffset.UtcNow.AddDays(-1);

        var staleId  = await SeedSessionAsync(staleTime);
        var recentId = await SeedSessionAsync(recentTime);

        var svc = BuildService();
        await svc.PruneAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        (await db.ClaudeSessions.AnyAsync(s => s.SessionId == staleId)).Should().BeFalse();
        (await db.ClaudeSessions.AnyAsync(s => s.SessionId == recentId)).Should().BeTrue();
    }

    // ── Machine agent retention ───────────────────────────────────────────────

    [Test]
    public async Task PruneAsync_StaleSession_MachineAgentIsRetained()
    {
        var machineId = "machine-keep";
        var staleTime = DateTimeOffset.UtcNow.AddDays(-(DbPruningService.RetentionDays + 1));
        await SeedSessionAsync(staleTime, machineId: machineId);

        var svc = BuildService();
        await svc.PruneAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var machineExists = await db.MachineAgents.AnyAsync(m => m.MachineId == machineId);
        machineExists.Should().BeTrue();
    }

    // ── Empty DB ──────────────────────────────────────────────────────────────

    [Test]
    public async Task PruneAsync_NoSessions_DoesNotThrow()
    {
        var svc = BuildService();
        await svc.Awaiting(s => s.PruneAsync(CancellationToken.None)).Should().NotThrowAsync();
    }
}
