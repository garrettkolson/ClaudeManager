using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class GpuHostServiceTests
{
    private SqliteConnection _conn = default!;
    private GpuHostService   _svc  = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn = conn;
        _svc  = new GpuHostService(factory);
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static GpuHostEntity MakeHost(string displayName = "GPU Box", string host = "192.168.1.10") =>
        new()
        {
            DisplayName = displayName,
            Host        = host,
            SshPort     = 22,
            SshUser     = "ubuntu",
            SshKeyPath  = "~/.ssh/id_rsa",
        };

    // ── GetAllAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task GetAllAsync_EmptyDb_ReturnsEmpty()
    {
        var result = await _svc.GetAllAsync();
        result.Should().BeEmpty();
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllHosts()
    {
        await _svc.AddAsync(MakeHost("Alpha"));
        await _svc.AddAsync(MakeHost("Beta"));

        var result = await _svc.GetAllAsync();
        result.Should().HaveCount(2);
    }

    [Test]
    public async Task GetAllAsync_OrderedByDisplayName()
    {
        await _svc.AddAsync(MakeHost("Zebra"));
        await _svc.AddAsync(MakeHost("Alpha"));

        var result = await _svc.GetAllAsync();
        result[0].DisplayName.Should().Be("Alpha");
        result[1].DisplayName.Should().Be("Zebra");
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [Test]
    public async Task GetByIdAsync_ExistingId_ReturnsHost()
    {
        var added = await _svc.AddAsync(MakeHost());

        var result = await _svc.GetByIdAsync(added.Id);
        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("GPU Box");
    }

    [Test]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _svc.GetByIdAsync(999);
        result.Should().BeNull();
    }

    // ── AddAsync ──────────────────────────────────────────────────────────────

    [Test]
    public async Task AddAsync_SetsAddedAt()
    {
        var before = DateTimeOffset.UtcNow;
        var added  = await _svc.AddAsync(MakeHost());
        var after  = DateTimeOffset.UtcNow;

        added.AddedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Test]
    public async Task AddAsync_AssignsAutoHostIdWhenBlank()
    {
        var host  = MakeHost();
        host.HostId = "";
        var added = await _svc.AddAsync(host);

        added.HostId.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task AddAsync_PreservesExplicitHostId()
    {
        var host  = MakeHost();
        host.HostId = "my-gpu-server";
        var added = await _svc.AddAsync(host);

        added.HostId.Should().Be("my-gpu-server");
    }

    [Test]
    public async Task AddAsync_PersistsAllFields()
    {
        var entity = new GpuHostEntity
        {
            DisplayName = "Test Server",
            Host        = "10.0.0.5",
            SshPort     = 2222,
            SshUser     = "admin",
            SshKeyPath  = "~/.ssh/gpu_key",
            SshPassword = null,
        };
        var added = await _svc.AddAsync(entity);

        var fetched = await _svc.GetByIdAsync(added.Id);
        fetched!.DisplayName.Should().Be("Test Server");
        fetched.Host.Should().Be("10.0.0.5");
        fetched.SshPort.Should().Be(2222);
        fetched.SshUser.Should().Be("admin");
        fetched.SshKeyPath.Should().Be("~/.ssh/gpu_key");
        fetched.SshPassword.Should().BeNull();
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task UpdateAsync_ChangesArePersisted()
    {
        var added = await _svc.AddAsync(MakeHost("Old Name"));
        added.DisplayName = "New Name";
        added.SshPort     = 2222;

        await _svc.UpdateAsync(added);

        var fetched = await _svc.GetByIdAsync(added.Id);
        fetched!.DisplayName.Should().Be("New Name");
        fetched.SshPort.Should().Be(2222);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task DeleteAsync_RemovesHost()
    {
        var added = await _svc.AddAsync(MakeHost());

        await _svc.DeleteAsync(added.Id);

        (await _svc.GetAllAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task DeleteAsync_NonExistentId_DoesNotThrow()
    {
        await _svc.Invoking(s => s.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    [Test]
    public async Task DeleteAsync_OnlyRemovesTargetHost()
    {
        var a = await _svc.AddAsync(MakeHost("A"));
        var b = await _svc.AddAsync(MakeHost("B"));

        await _svc.DeleteAsync(a.Id);

        var remaining = await _svc.GetAllAsync();
        remaining.Should().HaveCount(1);
        remaining[0].DisplayName.Should().Be("B");
    }
}
