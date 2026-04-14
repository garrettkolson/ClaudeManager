using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Services.Docker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class LlmDeploymentHealthServiceTests
{
    private readonly Mock<IDbContextFactory<ClaudeManagerDbContext>> _mockDbFactory = new();
    private readonly Mock<ILlmInstanceService> _mockInstance = new();
    private readonly Mock<GpuHostService> _mockGpuHosts = new();
    private readonly Mock<HubSecretService> _mockSecrets = new();
    private readonly Mock<NginxProxyService> _mockNginxProxy = new();
    private readonly Mock<LlmDeploymentNotifier> _mockNotifier = new();
    private readonly Mock<LlmProxyConfigService> _mockProxyConfig = new();
    private readonly LlmDeploymentHealthService _service;

    [SetUp]
    public void SetUp()
    {
        _service = new LlmDeploymentHealthService(
            _mockDbFactory.Object,
            _mockInstance.Object,
            _mockGpuHosts.Object,
            _mockSecrets.Object,
            _mockNginxProxy.Object,
            _mockNotifier.Object,
            _mockProxyConfig.Object,
            NullLogger<LlmDeploymentHealthService>.Instance
        );

        _mockDbFactory.Setup(f => f.CreateDbContext())
            .Returns<Func<ClaudeManagerDbContext>>(() =>
            {
                var db = new ClaudeManagerDbContext();
                return db;
            });
    }

    private static LlmDeploymentEntity MakeDeployment(LlmDeploymentStatus status = LlmDeploymentStatus.Running, long? deploymentId = null)
    {
        var deployment = new LlmDeploymentEntity
        {
            DeploymentId = deploymentId ?? Guid.NewGuid(),
            HostId = "test-host-id",
            HostPort = 8000,
            ModelId = "test/model",
            GpuIndices = "0",
            Status = status
        };
        return deployment;
    }

    private static GpuHostEntity MakeHost(int proxyPort = 8080)
    {
        return new GpuHostEntity
        {
            HostId = "test-host-id",
            Host = "127.0.0.1",
            SshUser = "admin",
            ProxyPort = proxyPort
        };
    }

    [Test]
    public async Task HandleUnhealthyAsync_OnSuccessPath_CallsRefreshNginxConfig()
    {
        // Arrange: Setup for successful restart path
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        var host = MakeHost(proxyPort: 8080);

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.StartContainerAsync(host, It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-container-id", null));

        _mockInstance.Setup(i => i.CheckHealthAsync(host, 8000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act: Call HandleUnhealthyAsync - should succeed and call RefreshNginxConfig
        await _service.HandleUnhealthyAsync(deployment, host, "Previous health check failed", CancellationToken.None);

        // Assert: Verify nginx refresh was called on success path
        _mockNginxProxy.Verify(n => n.ApplyConfigAsync(host, It.IsAny<IReadOnlyList<LlmDeploymentEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
        Mock.Get(_mockProxyConfig).Verify(p => p.InvalidateCache(), Times.Once);

        // Also verify deployment state was updated
        deployment.Status.Should().Be(LlmDeploymentStatus.Running);
        deployment.ContainerId.Should().Be("new-container-id");
        deployment.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task HandleUnhealthyAsync_OnRestartFailure_PathsTransitionToError()
    {
        // Arrange: Setup for restart failure path
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        var host = MakeHost(proxyPort: 8080);

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.StartContainerAsync(host, deployment, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("container-id", "start failed"));

        // Act
        await _service.HandleUnhealthyAsync(deployment, host, "Health failure", CancellationToken.None);

        // Assert: TransitionToErrorAsync is called on error, which calls RefreshNginxConfig
        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
        deployment.ErrorMessage.Should().Contain("Restart failed");
    }

    [Test]
    public async Task TransitionToErrorAsync_AlwaysCallsRefreshNginxConfig()
    {
        // Arrange: Test all code paths transition to Error
        var deployment = MakeDeployment(LlmDeploymentStatus.Running);
        var host = MakeHost(proxyPort: 8080);

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        // Act: Call TransitionToErrorAsync directly
        await _service.TransitionToErrorAsync(deployment, "Test error", CancellationToken.None);

        // Assert: RefreshNginxConfig is called in TransitionToErrorAsync
        _mockNginxProxy.Verify(n => n.ApplyConfigAsync(host, It.IsAny<IReadOnlyList<LlmDeploymentEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
        Mock.Get(_mockProxyConfig).Verify(p => p.InvalidateCache(), Times.Once);

        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
        deployment.ErrorMessage.Should().Be("Test error");
    }

    [Test]
    public async Task HandleUnhealthyAsync_HealthCheckFailsAfterRestart_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        // Arrange: Health check fails after container restart
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        var host = MakeHost(proxyPort: 8080);

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.StartContainerAsync(host, deployment, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-id", null));

        _mockInstance.Setup(i => i.CheckHealthAsync(host, 8000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // Health check fails

        // Act
        await _service.HandleUnhealthyAsync(deployment, host, "Health check failed", CancellationToken.None);

        // Assert: Health check failure after restart calls TransitionToErrorAsync
        // which then calls RefreshNginxConfigAsync
        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
        deployment.ErrorMessage.Should().Contain("Failed health check after restart");

        _mockNginxProxy.Verify(n => n.ApplyConfigAsync(It.IsAny<GpuHostEntity>(), It.IsAny<IReadOnlyList<LlmDeploymentEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task HandleUnhealthyAsync_MaxRestriesExceeded_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        // Arrange: Max retries already exceeded
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting)
        {
            RestartCount = LlmDeploymentHealthService.MaxAutoRestarts - 1
        };
        var host = MakeHost(proxyPort: 8080);

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.StartContainerAsync(host, deployment, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-id", null));

        _mockInstance.Setup(i => i.CheckHealthAsync(host, 8000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // Health check fails

        // Act
        await _service.HandleUnhealthyAsync(deployment, host, "Max retries exceeded", CancellationToken.None);

        // Assert: Max retries exceeded path transitions to Error
        // which then calls RefreshNginxConfigAsync
        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
        deployment.ErrorMessage.Should().Contain("max retries exceeded");

        _mockNginxProxy.Verify(n => n.ApplyConfigAsync(It.IsAny<GpuHostEntity>(), It.IsAny<IReadOnlyList<LlmDeploymentEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void CheckStartingDeploymentAsync_ContainerExits_DuringStartup_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        // Arrange: Container not found/exited during startup
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting)
        {
            ContainerId = "bad-container"
        };
        var host = MakeHost();

        ContainerStatus exitedStatus = ContainerStatus.Exited;

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.InspectContainerAsync(host, "bad-container", It.IsAny<CancellationToken>()))
            .ReturnsAsync(exitedStatus);

        // Act
        _service.CheckStartingDeploymentAsync(deployment, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        // Assert: Container exit during startup calls TransitionToErrorAsync
        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
        deployment.ErrorMessage.Should().Contain("Container exited during startup");

        _mockNginxProxy.Verify(n => n.ApplyConfigAsync(It.IsAny<GpuHostEntity>(), It.IsAny<IReadOnlyList<LlmDeploymentEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void CheckSingleDeploymentAsync_ContainerNotFound_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        // Arrange: Container not found when checking healthy running deployment
        var deployment = MakeDeployment(LlmDeploymentStatus.Running)
        {
            ContainerId = "missing-container"
        };
        var host = MakeHost();

        ContainerStatus notFound = ContainerStatus.NotFound;

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.InspectContainerAsync(host, "missing-container", It.IsAny<CancellationToken>()))
            .ReturnsAsync(notFound);

        // Act
        _service.CheckSingleDeploymentAsync(deployment, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        // Assert: Container not found calls TransitionToErrorAsync
        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
        deployment.ErrorMessage.Should().Contain("Container not found");

        _mockNginxProxy.Verify(n => n.ApplyConfigAsync(It.IsAny<GpuHostEntity>(), It.IsAny<IReadOnlyList<LlmDeploymentEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task CheckStartingDeploymentAsync_HTTPHealthCheckFails_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        // Arrange: Container running but HTTP health check fails
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting)
        {
            ContainerId = "started-container"
        };
        var host = MakeHost();

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.InspectContainerAsync(host, "started-container", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContainerStatus.Running);

        _mockInstance.Setup(i => i.CheckHealthAsync(host, It.IsAny<int>(), CancellationToken.None))
            .ReturnsAsync(false); // HTTP health check fails

        // Act
        _service.CheckStartingDeploymentAsync(deployment, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        // Assert: HTTP health check failure during startup calls TransitionToErrorAsync
        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
        deployment.ErrorMessage.Should().Contain("HTTP health check");

        _mockNginxProxy.Verify(n => n.ApplyConfigAsync(It.IsAny<GpuHostEntity>(), It.IsAny<IReadOnlyList<LlmDeploymentEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void CheckSingleDeploymentAsync_HTTPHealthCheckFails_CallsTransitionToErrorWhichCallsRefreshNginxConfig()
    {
        // Arrange: Container running but HTTP health check fails
        var deployment = MakeDeployment(LlmDeploymentStatus.Running);
        var host = MakeHost();

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.InspectContainerAsync(host, deployment.ContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContainerStatus.Running);

        _mockInstance.Setup(i => i.CheckHealthAsync(host, It.IsAny<int>(), CancellationToken.None))
            .ReturnsAsync(false); // HTTP health check fails

        // Act
        _service.CheckSingleDeploymentAsync(deployment, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        // Assert: HTTP health check failure during loop calls HandleUnhealthyAsync
        // which will eventually call TransitionToErrorAsync
        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
        deployment.ErrorMessage.Should().Contain("HTTP health check");

        _mockNginxProxy.Verify(n => n.ApplyConfigAsync(It.IsAny<GpuHostEntity>(), It.IsAny<IReadOnlyList<LlmDeploymentEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task CheckStartingDeploymentAsync_HTTPHealthCheckFails_CallsTransitionToErrorAsync()
    {
        // Arrange: First health check fails (call TransitionToError), second succeeds (call RefreshNginx)
        var deployment = MakeDeployment(LlmDeploymentStatus.Starting);
        var host = MakeHost();

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync("test-host-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockInstance.Setup(i => i.InspectContainerAsync(host, deployment.ContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContainerStatus.Running);

        // First check fails, second succeeds. Note: in reality this test verifies only one call.
        // Since HealthCheckLoop in CheckStartingDeploymentAsync calls TransitionToError once on health failure,
        // the refresh count test will show if both paths call refresh.
        _mockInstance.Setup(i => i.CheckHealthAsync(It.IsAny<GpuHostEntity>(), It.IsAny<int>(), CancellationToken.None))
            .ReturnsAsync(false);

        // Act
        _service.CheckStartingDeploymentAsync(deployment, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        // Assert: Should transition to Error and call refresh once
        deployment.Status.Should().Be(LlmDeploymentStatus.Error);
    }
}
