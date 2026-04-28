using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SweAfPortAllocatorTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private SweAfPortAllocator _allocator = default!;

    private static readonly SweAfConfigEntity DefaultConfig = new()
    {
        PortRangeStart = 8100,
        PortRangeEnd   = 8199,
    };

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn      = conn;
        _dbFactory = factory;
        _allocator = new SweAfPortAllocator();
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private async Task SeedJob(string externalId, int? allocatedPort, BuildStatus status)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.SweAfJobs.Add(new SweAfJobEntity
        {
            ExternalJobId  = externalId,
            Goal           = "G",
            RepoUrl        = "https://github.com/org/repo",
            AllocatedPort  = allocatedPort,
            Status         = status,
            CreatedAt      = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Test]
    public async Task EmptyDb_ReturnsRangeStart()
    {
        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8100);
    }

    [Test]
    public async Task FirstBlockInUse_ReturnsSecondBlock()
    {
        await SeedJob("job-1", 8100, BuildStatus.Running);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8103);
    }

    [Test]
    public async Task MultipleBlocksInUse_ReturnsNextFree()
    {
        await SeedJob("job-1", 8100, BuildStatus.Running);
        await SeedJob("job-2", 8103, BuildStatus.Running);
        await SeedJob("job-3", 8106, BuildStatus.Running);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8109);
    }

    // ── Terminal statuses don't block ports ───────────────────────────────────

    [Test]
    public async Task SucceededJob_DoesNotBlockPort()
    {
        await SeedJob("job-done", 8100, BuildStatus.Succeeded);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8100);
    }

    [Test]
    public async Task FailedJob_DoesNotBlockPort()
    {
        await SeedJob("job-fail", 8100, BuildStatus.Failed);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8100);
    }

    [Test]
    public async Task CancelledJob_DoesNotBlockPort()
    {
        await SeedJob("job-cancel", 8100, BuildStatus.Cancelled);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8100);
    }

    // ── Active statuses block ports ───────────────────────────────────────────

    [Test]
    public async Task RunningJob_BlocksPort()
    {
        await SeedJob("job-run", 8100, BuildStatus.Running);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8103);
    }

    [Test]
    public async Task QueuedJob_BlocksPort()
    {
        await SeedJob("job-q", 8100, BuildStatus.Queued);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8103);
    }

    [Test]
    public async Task WaitingJob_BlocksPort()
    {
        await SeedJob("job-wait", 8100, BuildStatus.Waiting);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8103);
    }

    // ── Block integrity: entire block skipped when any port is used ───────────

    [Test]
    public async Task MiddlePortOfBlockInUse_SkipsEntireBlock()
    {
        // Simulate a job that allocated base 8097 — its block covers 8097, 8098, 8099.
        // This means 8099 overlaps with the start of a block rooted at 8097.
        // More directly: seed a job at 8100 but check that offset +1 within a block also blocks
        // by seeding a job whose base port is 8098 (block: 8098, 8099, 8100).
        await SeedJob("job-overlap", 8098, BuildStatus.Running);

        // 8098 block covers 8098, 8099, 8100 → first clean block starts at 8101
        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8101);
    }

    [Test]
    public async Task JobWithNullAllocatedPort_Ignored()
    {
        await SeedJob("job-noport", null, BuildStatus.Running);

        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8100);
    }

    // ── Range boundaries ──────────────────────────────────────────────────────

    [Test]
    public async Task LastBlockInRange_IsAllocatable()
    {
        // Range 8100-8108 = exactly 3 blocks (8100, 8103, 8106); fill first two
        var config = new SweAfConfigEntity { PortRangeStart = 8100, PortRangeEnd = 8108 };
        await SeedJob("job-a", 8100, BuildStatus.Running);
        await SeedJob("job-b", 8103, BuildStatus.Running);

        var port = await _allocator.AllocatePortAsync(config, _dbFactory);
        port.Should().Be(8106);
    }

    [Test]
    public async Task RangeTooSmallForBlock_ReturnsNull()
    {
        // Range of 2 ports — too small for BlockSize=3
        var config = new SweAfConfigEntity { PortRangeStart = 8100, PortRangeEnd = 8101 };

        var port = await _allocator.AllocatePortAsync(config, _dbFactory);
        port.Should().BeNull();
    }

    [Test]
    public async Task AllBlocksExhausted_ReturnsNull()
    {
        // Range 8100-8108 = exactly 3 blocks (8100, 8103, 8106); fill all
        var config = new SweAfConfigEntity { PortRangeStart = 8100, PortRangeEnd = 8108 };
        await SeedJob("job-1", 8100, BuildStatus.Running);
        await SeedJob("job-2", 8103, BuildStatus.Running);
        await SeedJob("job-3", 8106, BuildStatus.Running);

        var port = await _allocator.AllocatePortAsync(config, _dbFactory);
        port.Should().BeNull();
    }

    [Test]
    public async Task ExactlyOneBlockRange_ReturnsItWhenFree()
    {
        var config = new SweAfConfigEntity { PortRangeStart = 5000, PortRangeEnd = 5002 };

        var port = await _allocator.AllocatePortAsync(config, _dbFactory);
        port.Should().Be(5000);
    }

    [Test]
    public async Task ExactlyOneBlockRange_ReturnsNullWhenUsed()
    {
        var config = new SweAfConfigEntity { PortRangeStart = 5000, PortRangeEnd = 5002 };
        await SeedJob("job-only", 5000, BuildStatus.Running);

        var port = await _allocator.AllocatePortAsync(config, _dbFactory);
        port.Should().BeNull();
    }

    // ── Mixed terminal + active ───────────────────────────────────────────────

    [Test]
    public async Task TerminalJobAtStart_ActiveJobAtSecond_ThirdBlockReturned()
    {
        await SeedJob("job-done", 8100, BuildStatus.Succeeded);
        await SeedJob("job-live", 8103, BuildStatus.Running);

        // 8100 block is free (terminal), 8103 block is used — should return 8100
        var port = await _allocator.AllocatePortAsync(DefaultConfig, _dbFactory);
        port.Should().Be(8100);
    }
}
