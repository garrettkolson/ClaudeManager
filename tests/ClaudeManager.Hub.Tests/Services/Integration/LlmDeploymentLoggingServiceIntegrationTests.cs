using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeManager.Hub.Tests.Services.Integration;

/// <summary>
/// Integration tests for LlmDeploymentLoggingService with cross-feature interactions.
/// Tests how LlmDeploymentLoggingService interacts with LlmInstanceService and dependency injection.
/// </summary>
public class LlmDeploymentLoggingServiceIntegrationTests
{
    private readonly LlmDeploymentLoggingService _svc;
    private readonly Mock<IServiceProvider> _serviceProvider;
    private readonly ConcurrentDictionary<long, string> _logRecords;

    public LlmDeploymentLoggingServiceIntegrationTests()
    {
        _serviceProvider = new Mock<IServiceProvider>();
        _logRecords = new ConcurrentDictionary<long, string>();

        // Setup mock database context
        var dbMock = new Mock<ClaudeManagerDbContext>();
        _serviceProvider.Setup(s => s.GetService<ClaudeManagerDbContext>())
            .Returns(dbMock.Object);

        // Setup mock LlmInstanceService
        var instanceServiceMock = new Mock<LlmInstanceService>();
        _serviceProvider.Setup(s => s.GetService<LlmInstanceService>())
            .Returns(instanceServiceMock.Object);

        _svc = new LlmDeploymentLoggingService(_serviceProvider.Object,
            NullLogger<LlmDeploymentLoggingService>.Instance);
    }

    #region GetLogsAsync Cross-Feature Tests

    [Test]
    public async Task GetLogsAsync_ValidDeployment_ReturnsLogsFromInstanceService()
    {
        // Arrange
        var host = new GpuHostEntity
        {
            HostId = 100
        };

        const string expectedLogs = "Test log line 1\nTest log line 2";

        // Mock database returns deployment
        _serviceProvider.Setup(s => s.GetService<ClaudeManager.Hub.Persistence.IClaudeManagerDbContext>())
            .Returns(new Mock<ClaudeManagerDbContext>().Object);

        var dbMock = new Mock<ClaudeManagerDbContext>();
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123"
        };

        dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _serviceProvider.Setup(s => s.GetService<ClaudeManager.Hub.Persistence.IClaudeManagerDbContext>())
            .Returns(dbMock.Object);

        // Mock instance service returns logs
        _serviceProvider.Setup(s => s.GetService<LlmInstanceService>())
            .Returns(new Mock<LlmInstanceService>().Object);

        var instanceServiceMock = new Mock<LlmInstanceService>();
        _serviceProvider.Setup(s => s.GetService<LlmInstanceService>())
            .Returns(instanceServiceMock.Object);

        instanceServiceMock
            .Setup(i => i.GetLogsAsync(host, "container123", 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((expectedLogs, null));

        // Act
        var result = await _svc.GetLogsAsync(1);

        // Assert
        result.Logs.Should().Be(expectedLogs);
        result.Error.Should().BeNull();
    }

    [Test]
    public async Task GetLogsAsync_NoDeployment_ReturnsNotFoundError()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = null
        };

        // Act
        var result = await _svc.GetLogsAsync(1);

        // Assert
        result.Logs.Should().BeNull();
        result.Error.Should().Contain("container");
    }

    [Test]
    public async Task GetLogsAsync_NoHost_ReturnsNotFoundError()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123"
        };

        _serviceProvider.Setup(s => s.GetService<ClaudeManager.Hub.Persistence.IClaudeManagerDbContext>())
            .Returns(new Mock<ClaudeManagerDbContext>().Object)
            .Returns(new Mock<ClaudeManagerDbContext>().Object);

        var dbMock = _serviceProvider.Object
            .GetService<ClaudeManager.Hub.Persistence.IClaudeManagerDbContext>();

        dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        // Act
        var result = await _svc.GetLogsAsync(1);

        // Assert
        result.Logs.Should().BeNull();
        result.Error.Should().Contain("host");
    }
    #endregion

    #region SubscribeAsync Integration Tests

    [Test]
    public async Task SubscribeAsync_InitialLogs_ThenUpdates()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123"
        };

        var initialLogs = "Initial log line\nSecond line";
        var updatedLogs = "Updated log line\nNew content";

        var dbMock = new Mock<ClaudeManagerDbContext>();
        dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var instanceServiceMock = new Mock<LlmInstanceService>();
        instanceServiceMock
            .Setup(i => i.GetLogsAsync(It.IsAny<GpuHostEntity>(), "container123", 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((initialLogs, null))
            .ReturnsAsync((updatedLogs, null));

        _serviceProvider.Setup(s => s.GetService<ClaudeManager.Hub.Persistence.IClaudeManagerDbContext>())
            .Returns(dbMock.Object);

        _serviceProvider.Setup(s => s.GetService<LlmInstanceService>())
            .Returns(instanceServiceMock.Object);

        // Act
        var receivedLines = new List<string>();
        int mockCallCount = 0;

        instanceServiceMock
            .Setup(i => i.GetLogsAsync(It.IsAny<GpuHostEntity>(), It.IsAny<string>(), 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                mockCallCount++;
                if (mockCallCount == 1)
                    return (initialLogs, null);
                return (updatedLogs, null);
            });

        await foreach (var line in _svc.SubscribeAsync(1, ct: default))
        {
            receivedLines.Add(line);
            if (receivedLines.Count >= 2)
                break;
        }

        // Assert
        receivedLines.Should().HaveCount(2);
        receivedLines.Should().Contain(initialLines: "Initial log line", "Second line");
    }
    #endregion
}
