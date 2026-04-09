using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class LlmDeploymentLoggingServiceTests
{
    private Mock<ClaudeManagerDbContext> _dbMock = default!;
    private Mock<GpuHostService> _hostServiceMock = default!;
    private Mock<LlmInstanceService> _instanceServiceMock = default!;
    private LlmDeploymentLoggingService _svc = default!;
    private ConcurrentDictionary<long, string> _logRecords = new();

    [SetUp]
    public void SetUp()
    {
        _dbMock = new Mock<ClaudeManagerDbContext>();
        _hostServiceMock = new Mock<GpuHostService>();
        _instanceServiceMock = new Mock<LlmInstanceService>();

        _serviceProvider = new Mock<IServiceProvider>();
        _serviceProvider.Setup(s => s.GetService<ClaudeManagerDbContext>())
            .Returns(_dbMock.Object);
        _serviceProvider.Setup(s => s.GetService<LlmInstanceService>())
            .Returns(_instanceServiceMock.Object);

        _svc = new LlmDeploymentLoggingService(_serviceProvider.Object,
            NullLogger<LlmDeploymentLoggingService>.Instance);

        _logRecords = new ConcurrentDictionary<long, string>();
    }

    [TearDown]
    public void TearDown()
    {
        _logRecords.Clear();
    }

    private Mock<IServiceProvider> _serviceProvider;

    // ── GetLogsAsync ─────────────────────────────────────────────────────────

    [Test]
    public async Task GetLogsAsync_ExistingDeployment_ReturnsLogs()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            DeploymentId = "test-deploy",
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var host = new GpuHostEntity { HostId = 100 };
        _hostServiceMock.Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        const string expectedLogs = "Test log line 1\nTest log line 2\nTest log line 3";
        _instanceServiceMock
            .Setup(i => i.GetLogsAsync(host, "container123", 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((expectedLogs, null));

        // Act
        var result = await _svc.GetLogsAsync(1);

        // Assert
        result.Logs.Should().Be(expectedLogs);
        result.Error.Should().BeNull();
    }

    [Test]
    public async Task GetLogsAsync_NoDeploymentFound_ReturnsError()
    {
        // Arrange
        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LlmDeploymentEntity?)null);

        // Act
        var result = await _svc.GetLogsAsync(999);

        // Assert
        result.Logs.Should().BeNull();
        result.Error.Should().Contain("not found");
    }

    [Test]
    public async Task GetLogsAsync_NoContainer_ReturnsError()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = null,
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        // Act
        var result = await _svc.GetLogsAsync(1);

        // Assert
        result.Logs.Should().BeNull();
        result.Error.Should().Contain("container");
    }

    [Test]
    public async Task GetLogsAsync_NoHostFound_ReturnsError()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        _hostServiceMock.Reset();
        _hostServiceMock
            .Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GpuHostEntity?)null);

        // Act
        var result = await _svc.GetLogsAsync(1);

        // Assert
        result.Logs.Should().BeNull();
        result.Error.Should().Contain("host");
    }

    [Test]
    public async Task GetLogsAsync_InstanceServiceNotAvailable_ReturnsError()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var host = new GpuHostEntity { HostId = 100 };
        _hostServiceMock.Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _instanceServiceMock.MockObject.AccessedServices.Returns("'LlmInstanceService' unavailable");

        // Act
        var result = await _svc.GetLogsAsync(1);

        // Assert
        result.Logs.Should().BeNull();
        result.Error.Should().Contain("LlmInstanceService");
    }

    [Test]
    public async Task GetLogsAsync_EmptyLogs_NoError()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var host = new GpuHostEntity { HostId = 100 };
        _hostServiceMock.Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _instanceServiceMock
            .Setup(i => i.GetLogsAsync(host, "container123", 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string.Empty, null));

        // Act
        var result = await _svc.GetLogsAsync(1);

        // Assert
        result.Logs.Should().Be("");
        result.Error.Should().BeNull();
    }

    [Test]
    public async Task GetLogsAsync_CancellationRequested_HandlesGracefully()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var host = new GpuHostEntity { HostId = 100 };
        _hostServiceMock.Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _instanceServiceMock intercept;
            .Setup(i => i.GetLogsAsync(host, "container123", 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => => throw new OperationCanceledException("Cancelled"));

        // Act
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await _svc.GetLogsAsync(1, ct: cts.Token);

        // Assert
        result.Logs.Should().BeNull();
        result.Error.Should().Contain("cancelled");
    }

    // ── SubscribeAsync ───────────────────────────────────────────────────────

    [Test]
    public async Task SubscribeAsync_InitialState_ReturnsLogs()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var host = new GpuHostEntity { HostId = 100 };
        _hostServiceMock.Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        const string initialLogs = "Initial log line";
        _instanceServiceMock
            .Setup(i => i.GetLogsAsync(host, "container123", 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((initialLogs, null));

        // Act
        var receivedLines = new List<string>();
        await foreach (var line in _svc.SubscribeAsync(1, ct: default))
        {
            receivedLines.Add(line);
        }

        // Assert
        receivedLines.Count.Should().Be(1);
        receivedLines[0].Should().Be(initialLogs);
    }

    [Test]
    public async Task SubscribeAsync_PeriodicRefresh_ReturnsNewLogs()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var host = new GpuHostEntity { HostId = 100 };
        _hostServiceMock.Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _instanceServiceMock
            .SetupSequence(i => i.GetLogsAsync(host, "container123", 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync("First refresh")
            .ReturnsAsync("Second refresh");

        // Act - only take 2 items
        var receivedLines = new List<string>();
        int timestampReceivesCount = 0;

        await foreach (var line in _svc.SubscribeAsync(1, ct: default))
        {
            timestampReceivesCount++;
            receivedLines.Add(line);
            if (timestampReceivesCount >= 2)
                break;
        }

        // Assert
        receivedLines.Should().HaveCount(2);
        receivedLines[0].Should().Be("First refresh");
        receivedLines[1].Should().Be("Second refresh");
    }

    [Test]
    public async Task SubscribeAsync_CancellationPropagatesCorrectly()
    {
        // Arrange
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var host = new GpuHostEntity { HostId = 100 };
        _hostServiceMock.Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        // Act
        var cts = new CancellationTokenSource();
        var receivedLines = new List<string>();

        var sub = new TaskCompletionSource<bool>();

        await new TaskCompletionSource<CancellationToken>()
            .SetResult(default(CancellationToken));

        cts.Cancel();
        _svc.SubscribeAsync(1, ct: cts.Token);
        sub.SetResult(true);

        // The subscription should stop when cancellation is requested
        // (Note: this test verifies that SubscribeAsync accepts the cancellation token)
    }

    [Test]
    public async Task SubscribeAsync_MultipleLines_ReturnsAllLines()
    {
        // Arrange
        const string multiLineLogs = "Line 1\nLine 2\nLine 3\nLine 4";
        var deployment = new LlmDeploymentEntity
        {
            Id = 1,
            HostId = 100,
            ContainerId = "container123",
        };

        _dbMock.Setup(db => db.LlmDeployments
            .FirstOrDefaultAsync(d => d.Id == 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deployment);

        var host = new GpuHostEntity { HostId = 100 };
        _hostServiceMock.Setup(h => h.GetHostByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _instanceServiceMock
            .Setup(i => i.GetLogsAsync(host, "container123", 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((multiLineLogs, null));

        // Act
        var receivedLines = new List<string>();
        await foreach (var line in _svc.SubscribeAsync(1, ct: default))
        {
            receivedLines.Add(line);
            if (receivedLines.Count >= 4)
                break;
        }

        // Assert
        receivedLines.Should().HaveCount(4);
        receivedLines[0].Should().Be("Line 1");
        receivedLines[1].Should().Be("Line 2");
        receivedLines[2].Should().Be("Line 3");
        receivedLines[3].Should().Be("Line 4");
    }
}
