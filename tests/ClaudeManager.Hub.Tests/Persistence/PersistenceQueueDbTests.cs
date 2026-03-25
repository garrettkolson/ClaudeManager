using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Tests.Helpers;
using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Persistence;

/// <summary>
/// Tests that PersistenceQueue.ExecuteAsync correctly writes each work-item type
/// to a real in-memory SQLite database.
/// Each test enqueues one item, starts the background service, then polls the DB
/// until the write lands (or a timeout expires).
///
/// Uses a named shared-cache in-memory database so the background service and the
/// polling assertions each get their own connection (avoiding SQLITE_BUSY conflicts
/// that occur when a single shared connection is used concurrently).
/// </summary>
[TestFixture]
public class PersistenceQueueDbTests
{
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private SqliteConnection _keeperConn = default!;
    private PersistenceQueue _queue = default!;
    private CancellationTokenSource _cts = default!;

    [SetUp]
    public async Task SetUp()
    {
        // Each test gets a unique named shared-cache in-memory database so that
        // multiple connections can access it simultaneously without lock conflicts.
        // A "keeper" connection stays open for the lifetime of the test to prevent
        // the named shared-cache database from being destroyed when other connections close.
        var dbName  = $"testdb_{Guid.NewGuid():N}";
        var connStr = $"DataSource={dbName};Mode=Memory;Cache=Shared";

        _keeperConn = new SqliteConnection(connStr);
        _keeperConn.Open();

        _dbFactory = CreateSharedCacheFactory(connStr);

        await using var setupDb = _dbFactory.CreateDbContext();
        await setupDb.Database.EnsureCreatedAsync();

        _queue = new PersistenceQueue(_dbFactory, NullLogger<PersistenceQueue>.Instance);
        _cts   = new CancellationTokenSource();
        _ = _queue.StartAsync(_cts.Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
        _queue.Dispose();
        _keeperConn.Close();
        await _keeperConn.DisposeAsync();
    }

    private static IDbContextFactory<ClaudeManagerDbContext> CreateSharedCacheFactory(string connStr)
    {
        var opts = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(connStr)
            .Options;
        return new TestDbContextFactory(opts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Polls <paramref name="check"/> until it returns true or timeout expires.</summary>
    private static async Task WaitForAsync(Func<Task<bool>> check, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await check()) return;
            await Task.Delay(20);
        }
        // Let the final assertion fail with a clear message
    }

    private async Task<ClaudeManagerDbContext> OpenDbAsync() =>
        await _dbFactory.CreateDbContextAsync();

    // Pre-seed a MachineAgentEntity so FK constraints on ClaudeSessions / StreamedLines pass.
    private async Task SeedAgentAsync(string machineId = TestData.MachineId)
    {
        await using var db = await OpenDbAsync();
        db.MachineAgents.Add(TestData.AgentEntity(machineId));
        await db.SaveChangesAsync();
    }

    private async Task SeedSessionAsync()
    {
        await SeedAgentAsync();
        await using var db = await OpenDbAsync();
        db.ClaudeSessions.Add(TestData.SessionEntity());
        await db.SaveChangesAsync();
    }

    // ── UpsertAgentWork ───────────────────────────────────────────────────────

    [Test]
    public async Task UpsertAgent_NewAgent_InsertsRow()
    {
        var agent = TestData.AgentEntity();
        _queue.EnqueueUpsertAgent(agent);

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            return await db.MachineAgents.AnyAsync(a => a.MachineId == agent.MachineId);
        });

        await using var db = await OpenDbAsync();
        var stored = await db.MachineAgents.SingleAsync(a => a.MachineId == agent.MachineId);
        stored.DisplayName.Should().Be(agent.DisplayName);
    }

    [Test]
    public async Task UpsertAgent_ExistingAgent_UpdatesDisplayName()
    {
        await SeedAgentAsync();

        var updated = TestData.AgentEntity();
        updated.DisplayName = "Updated Name";
        _queue.EnqueueUpsertAgent(updated);

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            return await db.MachineAgents
                .AnyAsync(a => a.MachineId == updated.MachineId && a.DisplayName == "Updated Name");
        });

        await using var db = await OpenDbAsync();
        var stored = await db.MachineAgents.SingleAsync(a => a.MachineId == updated.MachineId);
        stored.DisplayName.Should().Be("Updated Name");
    }

    [Test]
    public async Task UpsertAgent_ExistingAgent_DoesNotDuplicateRow()
    {
        await SeedAgentAsync();
        _queue.EnqueueUpsertAgent(TestData.AgentEntity());

        // Give the queue time to process
        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            return await db.MachineAgents.CountAsync(a => a.MachineId == TestData.MachineId) == 1;
        });

        await using var db = await OpenDbAsync();
        var count = await db.MachineAgents.CountAsync(a => a.MachineId == TestData.MachineId);
        count.Should().Be(1);
    }

    // ── UpsertSessionWork ─────────────────────────────────────────────────────

    [Test]
    public async Task UpsertSession_NewSession_InsertsRow()
    {
        await SeedAgentAsync();
        var session = TestData.SessionEntity();
        _queue.EnqueueUpsertSession(session);

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            return await db.ClaudeSessions.AnyAsync(s => s.SessionId == session.SessionId);
        });

        await using var db = await OpenDbAsync();
        var stored = await db.ClaudeSessions.SingleAsync(s => s.SessionId == session.SessionId);
        stored.MachineId.Should().Be(session.MachineId);
        stored.WorkingDirectory.Should().Be(session.WorkingDirectory);
    }

    [Test]
    public async Task UpsertSession_ExistingSession_UpdatesRow()
    {
        await SeedSessionAsync();

        var updated = TestData.SessionEntity();
        updated.InitialPrompt = "updated prompt";
        _queue.EnqueueUpsertSession(updated);

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            return await db.ClaudeSessions
                .AnyAsync(s => s.SessionId == updated.SessionId && s.InitialPrompt == "updated prompt");
        });

        await using var db = await OpenDbAsync();
        var stored = await db.ClaudeSessions.SingleAsync(s => s.SessionId == updated.SessionId);
        stored.InitialPrompt.Should().Be("updated prompt");
    }

    [Test]
    public async Task UpsertSession_ExistingSession_DoesNotDuplicateRow()
    {
        await SeedSessionAsync();
        _queue.EnqueueUpsertSession(TestData.SessionEntity());

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            return await db.ClaudeSessions.CountAsync(s => s.SessionId == TestData.SessionId) == 1;
        });

        await using var db = await OpenDbAsync();
        var count = await db.ClaudeSessions.CountAsync(s => s.SessionId == TestData.SessionId);
        count.Should().Be(1);
    }

    // ── WriteLineWork ─────────────────────────────────────────────────────────

    [Test]
    public async Task WriteLine_InsertsLineIntoDb()
    {
        await SeedSessionAsync();

        var lineEntity = new StreamedLineEntity
        {
            SessionId  = TestData.SessionId,
            Timestamp  = DateTimeOffset.UtcNow,
            Kind       = StreamLineKind.AssistantToken,
            Content    = "hello world",
        };
        _queue.EnqueueLine(lineEntity);

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            return await db.StreamedLines.AnyAsync(l => l.SessionId == TestData.SessionId);
        });

        await using var db = await OpenDbAsync();
        var stored = await db.StreamedLines.SingleAsync(l => l.SessionId == TestData.SessionId);
        stored.Content.Should().Be("hello world");
        stored.Kind.Should().Be(StreamLineKind.AssistantToken);
    }

    [Test]
    public async Task WriteLine_BumpsSessionLastActivityAt()
    {
        await SeedSessionAsync();

        var lineTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var lineEntity = new StreamedLineEntity
        {
            SessionId  = TestData.SessionId,
            Timestamp  = lineTime,
            Kind       = StreamLineKind.AssistantToken,
            Content    = "ping",
        };
        _queue.EnqueueLine(lineEntity);

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            var s = await db.ClaudeSessions.SingleAsync(s => s.SessionId == TestData.SessionId);
            return s.LastActivityAt >= lineTime;
        });

        await using var db = await OpenDbAsync();
        var session = await db.ClaudeSessions.SingleAsync(s => s.SessionId == TestData.SessionId);
        session.LastActivityAt.Should().BeCloseTo(lineTime, TimeSpan.FromSeconds(1));
    }

    // ── SessionStatusWork ─────────────────────────────────────────────────────

    [Test]
    public async Task SessionStatus_UpdatesStatusInDb()
    {
        await SeedSessionAsync();

        _queue.EnqueueSessionStatusChange(TestData.SessionId, SessionStatus.Ended, DateTimeOffset.UtcNow);

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            return await db.ClaudeSessions
                .AnyAsync(s => s.SessionId == TestData.SessionId && s.Status == SessionStatus.Ended);
        });

        await using var db = await OpenDbAsync();
        var session = await db.ClaudeSessions.SingleAsync(s => s.SessionId == TestData.SessionId);
        session.Status.Should().Be(SessionStatus.Ended);
    }

    [Test]
    public async Task SessionStatus_SetsEndedAt()
    {
        await SeedSessionAsync();

        var endTime = DateTimeOffset.UtcNow;
        _queue.EnqueueSessionStatusChange(TestData.SessionId, SessionStatus.Ended, endTime);

        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            var s = await db.ClaudeSessions.SingleAsync(s => s.SessionId == TestData.SessionId);
            return s.EndedAt.HasValue;
        });

        await using var db = await OpenDbAsync();
        var session = await db.ClaudeSessions.SingleAsync(s => s.SessionId == TestData.SessionId);
        session.EndedAt.Should().BeCloseTo(endTime, TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task SessionStatus_NullEndedAt_ClearsEndedAt()
    {
        await SeedSessionAsync();

        // First set an EndedAt
        _queue.EnqueueSessionStatusChange(TestData.SessionId, SessionStatus.Ended, DateTimeOffset.UtcNow);
        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            var s = await db.ClaudeSessions.SingleAsync(s => s.SessionId == TestData.SessionId);
            return s.EndedAt.HasValue;
        });

        // Now clear it
        _queue.EnqueueSessionStatusChange(TestData.SessionId, SessionStatus.Active, null);
        await WaitForAsync(async () =>
        {
            await using var db = await OpenDbAsync();
            var s = await db.ClaudeSessions.SingleAsync(s => s.SessionId == TestData.SessionId);
            return !s.EndedAt.HasValue;
        });

        await using var db = await OpenDbAsync();
        var session = await db.ClaudeSessions.SingleAsync(s => s.SessionId == TestData.SessionId);
        session.EndedAt.Should().BeNull();
    }
}
