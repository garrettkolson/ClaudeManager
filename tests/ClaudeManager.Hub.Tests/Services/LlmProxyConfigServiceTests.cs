using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class LlmProxyConfigServiceTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private Mock<IHubContext<AgentHub>> _hubContextMock = default!;
    private Mock<IClientProxy> _allClientsMock = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn      = conn;
        _dbFactory = factory;

        _allClientsMock = new Mock<IClientProxy>();
        _allClientsMock
            .Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(_allClientsMock.Object);

        _hubContextMock = new Mock<IHubContext<AgentHub>>();
        _hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private LlmProxyConfigService BuildSvc() =>
        new(
            new GpuHostService(_dbFactory),
            _dbFactory,
            _hubContextMock.Object,
            NullLogger<LlmProxyConfigService>.Instance);

    private async Task SeedHost(string hostId, string host, int? proxyPort)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.GpuHosts.Add(new GpuHostEntity
        {
            HostId      = hostId,
            DisplayName = hostId,
            Host        = host,
            ProxyPort   = proxyPort,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDeployment(string hostId, string modelId, LlmDeploymentStatus status)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.LlmDeployments.Add(new LlmDeploymentEntity
        {
            DeploymentId = Guid.NewGuid().ToString(),
            HostId       = hostId,
            ModelId      = modelId,
            Status       = status,
        });
        await db.SaveChangesAsync();
    }

    // ── GetConfigAsync: proxy URL computation ─────────────────────────────────

    [Test]
    public async Task GetConfigAsync_NoHosts_ReturnsEmptyProxyUrl()
    {
        var svc = BuildSvc();
        var cfg = await svc.GetConfigAsync();
        cfg.ProxyUrl.Should().BeEmpty();
    }

    [Test]
    public async Task GetConfigAsync_HostWithNoProxyPort_ReturnsEmptyProxyUrl()
    {
        await SeedHost("h1", "gpu-01.local", proxyPort: null);
        var svc = BuildSvc();
        var cfg = await svc.GetConfigAsync();
        cfg.ProxyUrl.Should().BeEmpty();
    }

    [Test]
    public async Task GetConfigAsync_HostWithProxyPort_ReturnsProxyUrl()
    {
        await SeedHost("h1", "gpu-01.local", proxyPort: 8080);
        var svc = BuildSvc();
        var cfg = await svc.GetConfigAsync();
        cfg.ProxyUrl.Should().Be("http://gpu-01.local:8080");
    }

    [Test]
    public async Task GetConfigAsync_MultipleHosts_UsesFirstWithProxy()
    {
        await SeedHost("h1", "no-proxy.local", proxyPort: null);
        await SeedHost("h2", "has-proxy.local", proxyPort: 9090);
        var svc = BuildSvc();
        var cfg = await svc.GetConfigAsync();
        cfg.ProxyUrl.Should().Contain("has-proxy.local:9090");
    }

    // ── GetConfigAsync: caching ───────────────────────────────────────────────

    [Test]
    public async Task GetConfigAsync_SecondCall_ReturnsCachedValue()
    {
        await SeedHost("h1", "gpu-01.local", proxyPort: 8080);
        var svc = BuildSvc();

        var first  = await svc.GetConfigAsync();
        // Mutate DB — cached result should not change
        await using (var db = _dbFactory.CreateDbContext())
        {
            var host = await db.GpuHosts.FirstAsync();
            host.ProxyPort = 9999;
            await db.SaveChangesAsync();
        }

        var second = await svc.GetConfigAsync();
        second.ProxyUrl.Should().Be(first.ProxyUrl);
        second.ProxyUrl.Should().Contain("8080");
    }

    // ── InvalidateCache ───────────────────────────────────────────────────────

    [Test]
    public async Task InvalidateCache_UpdatesCachedConfig()
    {
        await SeedHost("h1", "gpu-01.local", proxyPort: 8080);
        var svc = BuildSvc();

        // Prime cache with port 8080
        await svc.GetConfigAsync();

        // Change DB
        await using (var db = _dbFactory.CreateDbContext())
        {
            var host = await db.GpuHosts.FirstAsync();
            host.ProxyPort = 9999;
            await db.SaveChangesAsync();
        }

        // Invalidate forces recompute
        svc.InvalidateCache();

        var cfg = await svc.GetConfigAsync();
        cfg.ProxyUrl.Should().Contain("9999");
    }

    [Test]
    public async Task InvalidateCache_BroadcastsViaSignalR()
    {
        await SeedHost("h1", "gpu-01.local", proxyPort: 8080);
        var svc = BuildSvc();

        svc.InvalidateCache();

        _allClientsMock.Verify(c => c.SendCoreAsync(
            "llm-proxy-config",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void InvalidateCache_NoHosts_BroadcastsEmptyConfig()
    {
        var svc = BuildSvc();
        svc.InvalidateCache();

        _allClientsMock.Verify(c => c.SendCoreAsync(
            "llm-proxy-config",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task InvalidateCache_CalledTwice_BroadcastsTwice()
    {
        await SeedHost("h1", "gpu-01.local", proxyPort: 8080);
        var svc = BuildSvc();

        svc.InvalidateCache();
        svc.InvalidateCache();

        _allClientsMock.Verify(c => c.SendCoreAsync(
            "llm-proxy-config",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── ComputeConfig (sync wrapper) ──────────────────────────────────────────

    [Test]
    public async Task ComputeConfig_ReturnsCurrentDbState()
    {
        await SeedHost("h1", "gpu-sync.local", proxyPort: 7777);
        var svc = BuildSvc();

        var cfg = svc.ComputeConfig();
        cfg.ProxyUrl.Should().Be("http://gpu-sync.local:7777");
    }

    [Test]
    public void ComputeConfig_NoHosts_ReturnsEmptyUrl()
    {
        var svc = BuildSvc();
        svc.ComputeConfig().ProxyUrl.Should().BeEmpty();
    }
}
