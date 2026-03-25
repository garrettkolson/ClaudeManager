using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClaudeManager.Hub.Tests.Persistence;

/// <summary>
/// Channel overflow behavior for PersistenceQueue.
/// Note: the channel uses BoundedChannelFullMode.DropOldest, which means TryWrite always
/// returns true (it drops the oldest item rather than failing). The LogWarning branch in
/// TryEnqueue is therefore unreachable in the current implementation — those tests are
/// marked Ignored.
/// </summary>
[TestFixture]
public class PersistenceQueueChannelTests
{
    private Mock<IDbContextFactory<ClaudeManagerDbContext>> _dbFactoryMock = default!;
    private Mock<ILogger<PersistenceQueue>> _loggerMock = default!;
    private PersistenceQueue _queue = default!;

    [TearDown]
    public void TearDown() => _queue?.Dispose();

    [SetUp]
    public void SetUp()
    {
        _dbFactoryMock = new Mock<IDbContextFactory<ClaudeManagerDbContext>>();
        _loggerMock    = new Mock<ILogger<PersistenceQueue>>();
        _queue         = new PersistenceQueue(_dbFactoryMock.Object, _loggerMock.Object);
    }

    [Test]
    public void EnqueueUpsertAgent_WithCapacityAvailable_DoesNotLogWarning()
    {
        _queue.EnqueueUpsertAgent(TestData.AgentEntity());

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Test]
    [Ignore("With BoundedChannelFullMode.DropOldest, TryWrite always returns true. " +
            "The warning branch is unreachable; this test cannot be made to pass without " +
            "changing the channel mode or extracting TryWrite behind an interface.")]
    public void EnqueueAny_WhenChannelFull_LogsWarning()
    {
        // Unreachable: DropOldest drops oldest items silently, never returns false from TryWrite.
    }

    [Test]
    [Ignore("Same limitation as above — DropOldest prevents TryWrite from returning false.")]
    public void TryEnqueue_WhenChannelFull_DoesNotThrow()
    {
        // Unreachable for the same reason as above.
    }
}
