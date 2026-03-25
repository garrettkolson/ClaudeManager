using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Persistence;

[TestFixture]
public class StartupRecoveryServiceTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _factory = default!;
    private SessionStore _store = default!;

    [SetUp]
    public async Task SetUp()
    {
        // Use MigrateAsync so StartupRecoveryService can call MigrateAsync again (idempotent)
        (_factory, _conn) = await InMemoryDbHelper.CreateWithMigrationsAsync();
        (_store, _)       = new SessionStoreBuilder().Build();
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private StartupRecoveryService BuildService(IReadOnlyList<KnownMachineConfig>? knownMachines = null) =>
        new(_factory, _store, knownMachines ?? [], NullLogger<StartupRecoveryService>.Instance);

    // Seeds a machine + session pair directly into the DB
    private async Task SeedSessionAsync(
        string machineId, string sessionId,
        SessionStatus status, DateTimeOffset lastActivityAt)
    {
        await using var db = _factory.CreateDbContext();

        if (!await db.MachineAgents.AnyAsync(m => m.MachineId == machineId))
        {
            db.MachineAgents.Add(new MachineAgentEntity
            {
                MachineId        = machineId,
                DisplayName      = "Seeded Machine",
                Platform         = "linux",
                FirstConnectedAt = DateTimeOffset.UtcNow,
                LastConnectedAt  = DateTimeOffset.UtcNow,
                LastSeenAt       = DateTimeOffset.UtcNow,
            });
        }

        db.ClaudeSessions.Add(new ClaudeSessionEntity
        {
            SessionId        = sessionId,
            MachineId        = machineId,
            WorkingDirectory = "/projects/app",
            StartedAt        = lastActivityAt.AddHours(-1),
            Status           = status,
            LastActivityAt   = lastActivityAt,
        });

        await db.SaveChangesAsync();
    }

    // ── Session patching ──────────────────────────────────────────────────────

    [Test]
    public async Task StartAsync_ActiveSessions_PatchedToDisconnected()
    {
        await SeedSessionAsync("m1", "s1", SessionStatus.Active, DateTimeOffset.UtcNow);

        await BuildService().StartAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var session = await db.ClaudeSessions.FindAsync("s1");
        session!.Status.Should().Be(SessionStatus.Disconnected);
    }

    [Test]
    public async Task StartAsync_EndedSessions_NotPatched()
    {
        await SeedSessionAsync("m1", "s1", SessionStatus.Ended, DateTimeOffset.UtcNow);

        await BuildService().StartAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var session = await db.ClaudeSessions.FindAsync("s1");
        session!.Status.Should().Be(SessionStatus.Ended);
    }

    // ── Session loading into store ────────────────────────────────────────────

    [Test]
    public async Task StartAsync_RecentSession_LoadedIntoStore()
    {
        var recentActivity = DateTimeOffset.UtcNow.AddDays(-1);
        await SeedSessionAsync("m1", "recent-sess", SessionStatus.Ended, recentActivity);

        await BuildService().StartAsync(CancellationToken.None);

        _store.GetSession("m1", "recent-sess").Should().NotBeNull();
    }

    [Test]
    public async Task StartAsync_SessionOlderThan30Days_NotLoadedIntoStore()
    {
        var oldActivity = DateTimeOffset.UtcNow.AddDays(-31);
        await SeedSessionAsync("m1", "old-sess", SessionStatus.Ended, oldActivity);

        await BuildService().StartAsync(CancellationToken.None);

        _store.GetSession("m1", "old-sess").Should().BeNull();
    }

    [Test]
    public async Task StartAsync_RecentSession_MachineAlsoAddedToStore()
    {
        await SeedSessionAsync("m1", "s1", SessionStatus.Ended, DateTimeOffset.UtcNow);

        await BuildService().StartAsync(CancellationToken.None);

        _store.GetMachine("m1").Should().NotBeNull();
    }

    [Test]
    public async Task StartAsync_RestoredMachine_IsOffline()
    {
        await SeedSessionAsync("m1", "s1", SessionStatus.Ended, DateTimeOffset.UtcNow);

        await BuildService().StartAsync(CancellationToken.None);

        _store.GetMachine("m1")!.IsOnline.Should().BeFalse();
    }

    // ── Known machine seeding ─────────────────────────────────────────────────

    [Test]
    public async Task StartAsync_SeedsKnownMachineWithNoDbHistory()
    {
        var known = new KnownMachineConfig
        {
            MachineId   = "known-m1",
            DisplayName = "My Dev Box",
            Platform    = "linux",
            Host        = "192.168.1.10",
            SshUser     = "user",
            AgentCommand = "nohup ./agent &",
        };

        await BuildService([known]).StartAsync(CancellationToken.None);

        _store.GetMachine("known-m1").Should().NotBeNull();
        _store.GetMachine("known-m1")!.DisplayName.Should().Be("My Dev Box");
    }

    [Test]
    public async Task StartAsync_KnownMachineAlreadyRestoredFromDb_DbDataTakesPrecedence()
    {
        // Seed a machine from DB with a distinct display name
        await SeedSessionAsync("shared-id", "s1", SessionStatus.Ended, DateTimeOffset.UtcNow);

        var known = new KnownMachineConfig
        {
            MachineId    = "shared-id",
            DisplayName  = "Config Name",
            Platform     = "linux",
            Host         = "localhost",
            SshUser      = "u",
            AgentCommand = "cmd",
        };

        await BuildService([known]).StartAsync(CancellationToken.None);

        // DB-restored name ("Seeded Machine") wins over config name
        _store.GetMachine("shared-id")!.DisplayName.Should().Be("Seeded Machine");
    }

    [Test]
    public async Task StartAsync_EmptyKnownMachines_DoesNotThrow()
    {
        await BuildService([]).Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }
}
