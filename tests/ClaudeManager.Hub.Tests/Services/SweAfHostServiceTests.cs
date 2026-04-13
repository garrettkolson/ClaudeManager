using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SweAfHostServiceTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn      = conn;
        _dbFactory = factory;
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private SweAfHostService BuildSvc() =>
        new SweAfHostService(_dbFactory, NullLogger<SweAfHostService>.Instance);

    // ── GetCommands (static helper) ───────────────────────────────────────────

    [Test]
    public void GetCommands_ValidJson_ReturnsParsedList()
    {
        var host = new SweAfHostEntity
        {
            CommandsJson = """[{"Label":"Start","Command":"start.sh"},{"Label":"Stop","Command":"stop.sh"}]""",
        };
        var cmds = SweAfHostService.GetCommands(host);
        cmds.Should().HaveCount(2);
        cmds[0].Label.Should().Be("Start");
        cmds[1].Label.Should().Be("Stop");
    }

    [Test]
    public void GetCommands_NullJson_ReturnsEmpty()
    {
        var host = new SweAfHostEntity { CommandsJson = null };
        SweAfHostService.GetCommands(host).Should().BeEmpty();
    }

    [Test]
    public void GetCommands_InvalidJson_ReturnsEmpty()
    {
        var host = new SweAfHostEntity { CommandsJson = "not-json" };
        SweAfHostService.GetCommands(host).Should().BeEmpty();
    }

    // ── SerializeCommands (static helper) ─────────────────────────────────────

    [Test]
    public void SerializeCommands_FiltersBlanks()
    {
        var cmds = new List<SweAfHostCommand>
        {
            new() { Label = "Start", Command = "start.sh" },
            new() { Label = "",      Command = "ignored"  }, // blank label — omitted
        };
        var json = SweAfHostService.SerializeCommands(cmds);
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("Start");
        json.Should().NotContain("ignored");
    }

    [Test]
    public void SerializeCommands_AllBlank_ReturnsNull()
    {
        var cmds = new List<SweAfHostCommand>
        {
            new() { Label = "", Command = "x" },
        };
        SweAfHostService.SerializeCommands(cmds).Should().BeNull();
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_PersistsToDb()
    {
        var svc = BuildSvc();
        var cmds = new List<SweAfHostCommand>
        {
            new() { Label = "Start", Command = "docker compose up -d" },
        };
        var entity = await svc.CreateAsync("My Host", "server.local", "", commands: cmds);

        entity.Id.Should().BeGreaterThan(0);

        await using var db = _dbFactory.CreateDbContext();
        var stored = await db.SweAfHosts.FindAsync(entity.Id);
        stored.Should().NotBeNull();
        stored!.DisplayName.Should().Be("My Host");
        stored.AnthropicBaseUrl.Should().Be("server.local");
        stored.CommandsJson.Should().Contain("Start");
    }

    [Test]
    public async Task GetAllAsync_ReturnsOrderedByDisplayName()
    {
        var svc = BuildSvc();
        await svc.CreateAsync("Zebra Host", "z.local", "");
        await svc.CreateAsync("Alpha Host", "a.local", "");

        var all = await svc.GetAllAsync();
        all.Should().HaveCount(2);
        all[0].DisplayName.Should().Be("Alpha Host");
        all[1].DisplayName.Should().Be("Zebra Host");
    }

    [Test]
    public async Task DeleteAsync_RemovesFromDb()
    {
        var svc    = BuildSvc();
        var entity = await svc.CreateAsync("Temp", "temp.local", "");

        await svc.DeleteAsync(entity.Id);

        await using var db = _dbFactory.CreateDbContext();
        (await db.SweAfHosts.FindAsync(entity.Id)).Should().BeNull();
    }

    // ── Guard: no SSH auth on remote host ─────────────────────────────────────

    [Test]
    public async Task RunCommandAsync_RemoteHostWithNoAuth_ReturnsError()
    {
        var svc  = BuildSvc();
        var host = new SweAfHostEntity
        {
            Host    = "remote.example.com",
            SshPort = 22,
            SshUser = "user",
            // No SshKeyPath or SshPassword
        };
        var (ok, err) = await svc.RunCommandAsync(host, "start.sh", "Start");
        ok.Should().BeFalse();
        err.Should().Contain("SSH");
    }
}
