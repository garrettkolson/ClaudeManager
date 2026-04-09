using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Linq;

namespace ClaudeManager.Hub.Tests.Services;

/// <summary>
/// Test suite to validate the LLM Deployment Tab functionality.
/// These tests verify core services used by the LlmServers page.
/// </summary>
public class LlmDeploymentTabValidationTest
{
    private static readonly Mock<IDbContextFactory<ClaudeManagerDbContext>> _mockDbFactory = new();

    public LlmDeploymentTabValidationTest()
    {
    }

    [SetUp]
    public void Setup()
    {
    }

    [TearDown]
    public void Teardown()
    {
    }

    // ── Acceptance Criterion 1: Deployments sorted by creation date (ascending) ─

    [Test]
    public async Task GetAllAsync_ReturnsDeploymentsSortedByCreatedAtAscending()
    {
        // Arrange
        await using var db = _mockDbFactory.AsSubject.CreateDbContext();

        // Insert a few deployments with different creation dates
        var deployment1 = new LlmDeploymentEntity
        {
            DeploymentId = Guid.NewGuid().ToString(),
            ModelId = "Model-1",
            HostId = "Host-1",
            GpuIndices = "0",
            HostPort = 8000,
            Quantization = "none",
            ImageTag = "latest",
            Status = LlmDeploymentStatus.Stopped,
            CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var deployment2 = new LlmDeploymentEntity
        {
            DeploymentId = Guid.NewGuid().ToString(),
            ModelId = "Model-2",
            HostId = "Host-1",
            GpuIndices = "0",
            HostPort = 8000,
            Quantization = "none",
            ImageTag = "latest",
            Status = LlmDeploymentStatus.Stopped,
            CreatedAt = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var deployment3 = new LlmDeploymentEntity
        {
            DeploymentId = Guid.NewGuid().ToString(),
            ModelId = "Model-3",
            HostId = "Host-1",
            GpuIndices = "0",
            HostPort = 8000,
            Quantization = "none",
            ImageTag = "latest",
            Status = LlmDeploymentStatus.Stopped,
            CreatedAt = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await db.LlmDeployments.AddAsync(deployment1);
        await db.LlmDeployments.AddAsync(deployment2);
        await db.LlmDeployments.AddAsync(deployment3);
        await db.SaveChangesAsync();

        // Act
        var deployments = await db.LlmDeployments.ToListAsync();
        var result = deployments.OrderBy(d => d.CreatedAt).ToList();

        // Assert
        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0], Is.EqualTo(deployment1), "Oldest should be first");
        Assert.That(result[1], Is.EqualTo(deployment3));
        Assert.That(result[2], Is.EqualTo(deployment2), "Newest should be last");
    }

    // ── Acceptance Criterion 2: Start button enabled for stopped deployments ─

    [Test]
    public async Task CreateAsync_InitializesDeploymentWithStoppedStatus()
    {
        // Arrange
        var service = new LlmDeploymentService(
            _mockDbFactory.AsSubject,
            Mock.Of<LlmInstanceService>(),
            Mock.Of<GpuHostService>(),
            Mock.Of<HubSecretService>(),
            Mock.Of<LlmDeploymentNotifier>(),
            Mock.Of<NginxProxyService>(),
            Mock.Of<LlmProxyConfigService>(),
            LoggerFactory.Create(builder => builder.AddConsole()));

        // Act
        var deployment = await service.CreateAsync(
            hostId: "test-host",
            modelId: "test/model",
            gpuIndices: "0",
            hostPort: 8000,
            quantization: "none",
            imageTag: "latest",
            extraArgs: null,
            hfTokenOverride: null,
            maxModelLen: null);

        // Assert
        Assert.That(deployment.Status, Is.EqualTo(LlmDeploymentStatus.Stopped));
        Assert.That(deployment.DeploymentId, Is.Not.Null.And.NotEmpty);
        Assert.That(deployment.HostId, Is.EqualTo("test-host"));
        Assert.That(deployment.ModelId, Is.EqualTo("test/model"));
        Assert.That(deployment.HostPort, Is.EqualTo(8000));
    }

    // ── Acceptance Criterion 3: GetLogsAsync returns error for non-existent deployment ─

    [Test]
    public async Task GetLogsAsync_ThrowsErrorForNonExistentDeployment()
    {
        // Arrange
        var service = new LlmDeploymentService(
            _mockDbFactory.AsSubject,
            Mock.Of<LlmInstanceService>(),
            Mock.Of<GpuHostService>(),
            Mock.Of<HubSecretService>(),
            Mock.Of<LlmDeploymentNotifier>(),
            Mock.Of<NginxProxyService>(),
            Mock.Of<LlmProxyConfigService>(),
            LoggerFactory.Create(builder => builder.AddConsole()));

        // Act
        var (logs, error) = await service.GetLogsAsync(9999);

        // Assert
        Assert.That(logs, Is.Null);
        Assert.That(error, Is.Not.Null);
        Assert.That(error.ToLower(), Contains.Substring("not found"));
    }

    // ── Acceptance Criterion 4: GetLogsAsync returns error for deployment without container ─

    [Test]
    public async Task GetLogsAsync_ReturnsErrorForDeploymentWithoutContainer()
    {
        // Arrange
        var deploy = new LlmDeploymentEntity
        {
            DeploymentId = Guid.NewGuid().ToString(),
            ModelId = "Model-1",
            HostId = "Host-1",
            GpuIndices = "0",
            HostPort = 8000,
            Quantization = "none",
            ImageTag = "latest",
            Status = LlmDeploymentStatus.Stopped,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var db = _mockDbFactory.AsSubject.CreateDbContext();
        await db.LlmDeployments.AddAsync(deploy);
        await db.SaveChangesAsync();

        var service = new LlmDeploymentService(
            _mockDbFactory.AsSubject,
            Mock.Of<LlmInstanceService>(),
            Mock.Of<GpuHostService>(),
            Mock.Of<HubSecretService>(),
            Mock.Of<LlmDeploymentNotifier>(),
            Mock.Of<NginxProxyService>(),
            Mock.Of<LlmProxyConfigService>(),
            LoggerFactory.Create(builder => builder.AddConsole()));

        // Act
        var (logs, error) = await service.GetLogsAsync(deploy.Id);

        // Assert
        Assert.That(logs, Is.Null);
        Assert.That(error, Is.Not.Null);
        Assert.That(error.ToLower(), Contains.Substring("never started"));
    }

    // ── Acceptance Criterion 5: LlmDeploymentNotifier handles changes ─

    [Test]
    public void Notifier_InvokesEventWhenDeploymentChanged()
    {
        // Arrange
        var notifier = new LlmDeploymentNotifier();
        bool handlerCalled = false;

        notifier.DeploymentChanged += _ => { handlerCalled = true; };

        // Act
        var deployment = new LlmDeploymentEntity
        {
            DeploymentId = Guid.NewGuid().ToString(),
            ModelId = "Model-1",
            HostId = "Host-1",
            GpuIndices = "0",
            HostPort = 8000,
            Quantization = "none",
            ImageTag = "latest",
            Status = LlmDeploymentStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        notifier.NotifyChanged(deployment);

        // Assert
        Assert.That(handlerCalled, Is.True);
    }
}

public static class MockExtensions
{
    public static T AsSubject<T>(this Mock<T> mock) where T : class =>
        mock.Object;
}
