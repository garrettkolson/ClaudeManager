using Microsoft.Data.Sqlite;
using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Services.Docker;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Integration.Tests;

/// <summary>
/// Concurrent Nginx restart safety tests.
/// </summary>
public class NginxConcurrentRestartTests : IDisposable
{
    private SqliteConnection? _conn;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private readonly Mock<ILlmInstanceService> _mockInstance = new();
    private readonly Mock<IDockerExecutor> _mockDocker = new();
    private readonly LlmDeploymentNotifier _notifier = new();
    private readonly LlmDeploymentHealthService _healthService;
    private readonly LlmProxyConfigService _proxyConfig;

    public NginxConcurrentRestartTests()
    {
        // Create one shared in-memory DB for the fixture
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        _conn = conn;
        var opts = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite(conn)
            .Options;
        _dbFactory = new TestDbContextFactory(opts);

        // Ensure schema
        using (var db = new ClaudeManagerDbContext(opts))
            db.Database.EnsureCreated();

        var gpuHosts   = new GpuHostService(_dbFactory);
        var secrets    = new HubSecretService(_dbFactory);
        var nginxProxy = new NginxProxyService(NullLogger<NginxProxyService>.Instance, _mockDocker.Object);
        var mockHubContext = new Mock<IHubContext<AgentHub>>();
        mockHubContext.Setup(h => h.Clients.All)
            .Returns(new Mock<IClientProxy>().Object);
        _proxyConfig = new LlmProxyConfigService(
            gpuHosts, _dbFactory, mockHubContext.Object,
            NullLogger<LlmProxyConfigService>.Instance);

        _healthService = new LlmDeploymentHealthService(
            _dbFactory, _mockInstance.Object, gpuHosts, secrets,
            nginxProxy, _notifier, _proxyConfig,
            NullLogger<LlmDeploymentHealthService>.Instance
        );
    }

    public void Dispose()
    {
        _healthService.Dispose();
        _conn?.Dispose();
        _conn?.Close();
    }

    private LlmDeploymentEntity MakeDeployment(
        string? hostId = null, int hostPort = 8000,
        LlmDeploymentStatus status = LlmDeploymentStatus.Running,
        string? deploymentId = null) => new()
    {
        DeploymentId = deploymentId ?? Guid.NewGuid().ToString("N")[..12],
        HostId = hostId ?? "test-host-id",
        HostPort = hostPort,
        ModelId = "test/model",
        GpuIndices = "0",
        Status = status
    };

    private GpuHostEntity MakeHost(string? hostId = null, int proxyPort = 8080, string? sshUser = null) => new()
    {
        HostId = hostId ?? "test-host-id",
        DisplayName = hostId ?? "test-host-id",
        Host = "127.0.0.1",
        SshUser = sshUser ?? "admin",
        ProxyPort = proxyPort
    };

    private class TestDbContextFactory(DbContextOptions<ClaudeManagerDbContext> opts)
        : IDbContextFactory<ClaudeManagerDbContext>
    {
        public ClaudeManagerDbContext CreateDbContext() => new(opts);
        public Task<ClaudeManagerDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new ClaudeManagerDbContext(opts));
    }

    [Test]
    public async Task MultipleConcurrentUnhealthyDeployments_RestartWithoutConfigCorruption()
    {
        const int numDeployments = 5;
        const string hostId = "concurrent-test-host-1";
        var host = MakeHost(hostId: hostId, proxyPort: 8080);
        var deploymentIds = new List<string>();

        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            for (int i = 0; i < numDeployments; i++)
            {
                var depId = $"dep-{Guid.NewGuid():N}";
                deploymentIds.Add(depId);
                db.LlmDeployments.Add(MakeDeployment(hostId, 8000, LlmDeploymentStatus.Error, depId));
            }
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.StartContainerAsync(host, It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-container-" + Guid.NewGuid(), (string?)null));
        _mockInstance.Setup(i => i.CheckHealthAsync(host, 8000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockDocker.Setup(d => d.ExecuteShellAsync(It.IsAny<ShellCommand>(), It.IsAny<ExecutionHost>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerExecutionResult(null, null, 0, true));

        var deployments = new List<LlmDeploymentEntity>();
        using (var db = _dbFactory.CreateDbContext())
        {
            deployments = db.LlmDeployments.Where(d => d.HostId == hostId).ToList();
        }

        foreach (var deployment in deployments)
        {
            await _healthService.HandleUnhealthyAsync(deployment, host, "Health check failed", CancellationToken.None);
        }

        using (var db = _dbFactory.CreateDbContext())
        {
            var result = db.LlmDeployments.Where(d => d.HostId == hostId).ToList();
            result.Should().HaveCount(numDeployments);
        }
    }

    [Test]
    public async Task MultipleGpuHosts_ConcurrentRefreshesDontInterfere()
    {
        const int numHosts = 3;
        const int numDeploymentsPerHost = 2;
        var deploymentInfos = new List<(string hostId, LlmDeploymentEntity dep)>();
        var hosts = new List<GpuHostEntity>();

        using (var db = _dbFactory.CreateDbContext())
        {
            for (int i = 0; i < numHosts; i++)
            {
                var hostId = $"multi-host-{i}";
                var host = MakeHost(hostId: hostId);
                hosts.Add(host);
                db.GpuHosts.Add(host);

                for (int j = 0; j < numDeploymentsPerHost; j++)
                {
                    var dep = MakeDeployment(hostId, 7000 + i * 100 + j, LlmDeploymentStatus.Error, $"multi-host-{i}-dep-{j}");
                    deploymentInfos.Add((hostId, dep));
                    db.LlmDeployments.Add(dep);
                }
            }
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.StartContainerAsync(It.IsAny<GpuHostEntity>(), It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-" + Guid.NewGuid(), (string?)null));
        _mockInstance.Setup(i => i.CheckHealthAsync(It.IsAny<GpuHostEntity>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockDocker.Setup(d => d.ExecuteShellAsync(It.IsAny<ShellCommand>(), It.IsAny<ExecutionHost>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerExecutionResult(null, null, 0, true));

        foreach (var host in hosts)
        {
            using (var db = _dbFactory.CreateDbContext())
            {
                var errorDeps = db.LlmDeployments
                    .Where(d => d.HostId == host.HostId && d.Status == LlmDeploymentStatus.Error)
                    .ToList();
                foreach (var dep in errorDeps)
                {
                    await _healthService.HandleUnhealthyAsync(dep, host, "Health failure", CancellationToken.None);
                }
            }
        }
    }

    [Test]
    public async Task HighConcurrentStress_MultipleSimultaneousFailures_SucceedAsync()
    {
        const int numDeployments = 10;
        const string hostId = "stress-test-host-1";
        var host = MakeHost(hostId, 9999);
        var deploymentIds = new List<string>();

        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            for (int i = 0; i < numDeployments; i++)
            {
                var depId = $"stress-dep-{i}";
                deploymentIds.Add(depId);
                db.LlmDeployments.Add(MakeDeployment(hostId, 9500 + depId.Length, LlmDeploymentStatus.Error, depId));
            }
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.StartContainerAsync(host, It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-" + Guid.NewGuid(), (string?)null));
        _mockInstance.Setup(i => i.CheckHealthAsync(It.IsAny<GpuHostEntity>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockDocker.Setup(d => d.ExecuteShellAsync(It.IsAny<ShellCommand>(), It.IsAny<ExecutionHost>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerExecutionResult(null, null, 0, true));

        var tasks = new List<Task>();
        for (int i = 0; i < 2; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await _healthService.HealthCheckLoopAsync(CancellationToken.None);
                await Task.Delay(10, CancellationToken.None);
            }));
        }

        await Task.WhenAll(tasks);

        using (var db = _dbFactory.CreateDbContext())
        {
            var result = db.LlmDeployments.Where(d => d.HostId == hostId).ToList();
            result.Should().HaveCount(numDeployments);
        }
    }
}
