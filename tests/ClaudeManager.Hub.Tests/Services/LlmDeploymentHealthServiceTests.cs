using ClaudeManager.Hub.Hubs;
using Microsoft.Data.Sqlite;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Services.Docker;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class LlmDeploymentHealthServiceTests
{
    private SqliteConnection? _conn;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private readonly Mock<ILlmInstanceService> _mockInstance = new();
    private readonly Mock<IDockerExecutor> _mockDocker = new();
    private LlmDeploymentHealthService _service = default!;
    private NginxProxyService _nginxProxy = default!;
    private LlmDeploymentNotifier _notifier = new();

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn = conn;
        _dbFactory = factory;

        var gpuHosts   = new GpuHostService(_dbFactory);
        var secrets    = new HubSecretService(_dbFactory);
        _nginxProxy    = new NginxProxyService(NullLogger<NginxProxyService>.Instance, _mockDocker.Object);
        var mockHubContext = new Mock<IHubContext<AgentHub>>();
        mockHubContext.Setup(h => h.Clients.All)
            .Returns(new Mock<IClientProxy>().Object);
        var proxyConfig = new LlmProxyConfigService(
            gpuHosts, _dbFactory, mockHubContext.Object,
            NullLogger<LlmProxyConfigService>.Instance);

        _service = new LlmDeploymentHealthService(
            _dbFactory, _mockInstance.Object, gpuHosts, secrets,
            _nginxProxy, _notifier, proxyConfig,
            NullLogger<LlmDeploymentHealthService>.Instance
        );

        _mockDocker.Setup(d => d.ExecuteShellAsync(It.IsAny<ShellCommand>(), It.IsAny<ExecutionHost>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerExecutionResult(null, null, 0, true));
        _mockInstance.Setup(i => i.StartContainerAsync(It.IsAny<GpuHostEntity>(), It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("", "auto-restart"));
    }

    [TearDown]
    public void Teardown()
    {
        _service.Dispose();
        _conn?.Dispose();
        _conn?.Close();
    }

    private static LlmDeploymentEntity MakeDeployment(
        LlmDeploymentStatus status = LlmDeploymentStatus.Running,
        string? hostId = null) => new()
    {
        DeploymentId = Guid.NewGuid().ToString("N")[..12],
        ContainerId  = "test-container-id",
        HostId       = hostId ?? "test-host-id",
        HostPort     = 8000,
        ModelId      = "test/model",
        GpuIndices   = "0",
        Status       = status
    };

    private static GpuHostEntity MakeHost(int proxyPort = 8080, string? hostId = null) => new()
    {
        HostId = hostId ?? "test-host-id",
        DisplayName = hostId ?? "test-host-id",
        Host   = "127.0.0.1",
        SshUser = "admin",
        ProxyPort = proxyPort
    };

    [Test]
    public async Task HandleUnhealthyAsync_OnSuccessPath_CallsRefreshNginxConfig()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        var host = MakeHost(proxyPort: 8080);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.StartContainerAsync(host, It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-container-id", null));
        _mockInstance.Setup(i => i.CheckHealthAsync(host, 8000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.HandleUnhealthyAsync(deployment, host, "Previous health check failed", CancellationToken.None);

        // Reload from DB
        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Running);
            loaded.ContainerId.Should().Be("new-container-id");
            loaded.ErrorMessage.Should().BeNull();
        }
    }

    [Test]
    public async Task HandleUnhealthyAsync_OnRestartFailure_PathsTransitionToError()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        var host = MakeHost(proxyPort: 8080);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.StartContainerAsync(host, deployment, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("container-id", "start failed"));

        await _service.HandleUnhealthyAsync(deployment, host, "Health failure", CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
            loaded.ErrorMessage.Should().Contain("Restart failed");
        }
    }

    [Test]
    public async Task TransitionToErrorAsync_AlwaysCallsRefreshNginxConfig()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Running);
        var host = MakeHost(proxyPort: 8080);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        await _service.TransitionToErrorAsync(deployment, "Test error", CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
            loaded.ErrorMessage.Should().Be("Test error");
        }
    }

    [Test]
    public async Task HandleUnhealthyAsync_HealthCheckFailsAfterRestart_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        var host = MakeHost(proxyPort: 8080);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.StartContainerAsync(host, deployment, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-id", null));
        _mockInstance.Setup(i => i.CheckHealthAsync(host, 8000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _service.HandleUnhealthyAsync(deployment, host, "Health check failed", CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
            loaded.ErrorMessage.Should().Contain("Failed health check after restart");
        }
    }

    [Test]
    public async Task HandleUnhealthyAsync_MaxRestriesExceeded_TransitionsToError()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        deployment.RestartCount = LlmDeploymentHealthService.MaxAutoRestarts;
        var host = MakeHost(proxyPort: 8080);
        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        await _service.HandleUnhealthyAsync(deployment, host, "Max retries exceeded", CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
            loaded.ErrorMessage.Should().Contain("max retries exceeded");
        }
    }

    [Test]
    public async Task CheckStartingDeploymentAsync_ContainerExits_DuringStartup_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        deployment.ContainerId = "bad-container";
        var host = MakeHost();

        ContainerStatus exitedStatus = ContainerStatus.Exited;

        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.InspectContainerAsync(host, "bad-container", It.IsAny<CancellationToken>()))
            .ReturnsAsync(exitedStatus);

        await _service.CheckStartingDeploymentAsync(deployment, CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
            loaded.ErrorMessage.Should().Contain("Container exited during startup");
        }
    }

    [Test]
    public async Task CheckSingleDeploymentAsync_ContainerNotFound_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Running);
        deployment.ContainerId = "missing-container";
        var host = MakeHost();

        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.InspectContainerAsync(host, "missing-container", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContainerStatus?)null);

        await _service.CheckSingleDeploymentAsync(deployment, CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
            loaded.ErrorMessage.Should().Contain("Restart failed");
        }
    }

    [Test]
    public async Task CheckStartingDeploymentAsync_HTTPHealthCheckFails_TransitionsToError()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        deployment.ContainerId = "started-container";
        var host = MakeHost();

        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.InspectContainerAsync(host, "started-container", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContainerStatus.Running);

        await _service.CheckStartingDeploymentAsync(deployment, CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
        }
    }

    [Test]
    public async Task CheckSingleDeploymentAsync_HTTPHealthCheckFails_RestartFails_TransitionsToError()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Running);
        var host = MakeHost();

        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.InspectContainerAsync(host, deployment.ContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContainerStatus.Running);
        _mockInstance.Setup(i => i.CheckHealthAsync(host, It.IsAny<int>(), CancellationToken.None))
            .ReturnsAsync(false);

        await _service.CheckSingleDeploymentAsync(deployment, CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
            loaded.ErrorMessage.Should().Contain("Restart failed");
        }
    }

    [Test]
    public async Task CheckStartingDeploymentAsync_HTTPHealthCheckFails_CallsTransitionToErrorAsync()
    {
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        var host = MakeHost();

        using (var db = _dbFactory.CreateDbContext())
        {
            db.GpuHosts.Add(host);
            db.LlmDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        _mockInstance.Setup(i => i.InspectContainerAsync(host, deployment.ContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContainerStatus.Running);
        _mockInstance.Setup(i => i.CheckHealthAsync(It.IsAny<GpuHostEntity>(), It.IsAny<int>(), CancellationToken.None))
            .ReturnsAsync(false);

        await _service.CheckStartingDeploymentAsync(deployment, CancellationToken.None);

        using (var db = _dbFactory.CreateDbContext())
        {
            var loaded = await db.LlmDeployments.FindAsync([deployment.Id]);
            loaded!.Status.Should().Be(LlmDeploymentStatus.Error);
        }
    }
}
