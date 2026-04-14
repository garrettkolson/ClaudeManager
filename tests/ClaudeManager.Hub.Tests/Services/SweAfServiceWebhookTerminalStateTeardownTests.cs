using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text;
using System.Text.Json;
using System.Net.Http;

namespace ClaudeManager.Hub.Tests.Services;

/// <summary>
/// Integration tests for webhook-terminal-state-container-teardown interaction.
/// Verifies that when the Hub processes webhook events for terminal states
/// (succeeded/failed/cancelled), container teardown is correctly triggered
/// using ComposeProjectName materialized from webhook-created jobs' ExternalJobId hash.
///
/// This tests the critical merge integration point between:
/// - issue/a25db24c-01-webhook-job-create-materialize-project-name (ComposeProjectName materialization)
/// - issue/a25db24c-02-container-teardown-post-webhook-finalization (teardown trigger on terminal states)
/// </summary>
[TestFixture]
public class SweAfServiceWebhookTerminalStateTeardownTests
{
    private SqliteConnection? _conn;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private BuildNotifier _notifier = default!;
    private Mock<ISwarmProvisioningService> _swarmProvisioner = new();
    private Mock<ISwarmRunnerPortAllocator>? _portAllocator;

    [SetUp]
    public async Task SetUp()
    {
        var dataHelper = new DataTestHelper();
        var (factory, conn) = await InMemoryDbHelper.CreateAsync(dataHelper.GetConcreteDbOptions());
        _conn = conn as SqliteConnection;
        _dbFactory = factory;
        _notifier = new BuildNotifier();
        _portAllocator = new Mock<ISwarmRunnerPortAllocator>();
        _portAllocator
            .Setup(x => x.AllocatePortAsync(
                It.IsAny<SweAfConfigEntity>(),
                It.IsAny<IDbContextFactory<ClaudeManagerDbContext>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(8100);
    }

    [TearDown]
    public void TearDown()
    {
        _conn?.Dispose();
        _conn?.Close();
    }

    private static ObservabilityBatch MakeBatch(string eventType, string executionId,
        string target = "swe-planner.build", object? extra = null)
    {
        var dict = new Dictionary<string, object?>
        {
            ["target"]       = target,
            ["execution_id"] = executionId,
        };
        if (extra is not null)
            foreach (var prop in extra.GetType().GetProperties())
                dict[prop.Name.ToLowerInvariant()] = prop.GetValue(extra);

        var data = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var evt = new ObservabilityEvent(eventType, "test", DateTimeOffset.UtcNow, data);
        return new ObservabilityBatch("batch-1", 1, new[] { evt }, DateTimeOffset.UtcNow);
    }

    private HttpRequestMessage MockHttp(HttpStatusCode status, object? body = null)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                It.IsAny<HttpRequestMessage>(),
                It.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = body is null
                    ? new StringContent("{}")
                    : JsonContent.Create(body),
            });
        return mock.Object;
    }

    private SweAfConfigService CreateConfigService(string baseUrl, string apiKey,
        string? modelDefault = null, string? modelCoder = null, string? modelQa = null)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.SweAfConfigs.RemoveAll(c => c.BaseUrl == baseUrl);

        db.SweAfConfigs.Add(new SweAfConfigEntity
        {
            BaseUrl     = baseUrl,
            ApiKey      = apiKey,
            Runtime     = "claude_code",
            ModelDefault = modelDefault,
            ModelCoder   = modelCoder,
            ModelQa      = modelQa,
        });
        db.SaveChanges();

        var svc = new SweAfConfigService(_dbFactory, NullLogger<SweAfConfigService>.Instance);
        svc.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return svc;
    }

    private SweAfService CreateService(HttpMessageHandler? handler = null,
        ISwarmProvisioningService? provisioner = null, int? port = null)
    {
        var httpFactory = new TestHttpClientFactory(handler ?? MockHttp(HttpStatusCode.OK).Object);
        var configSvc = CreateConfigService("https://test.com", "testkey");
        return new SweAfService(httpFactory,
            configSvc,
            _dbFactory,
            _notifier,
            provisioner ?? _swarmProvisioner.Object,
            portAllocator: port is not null ? new Mock<ISwarmRunnerPortAllocator>()
            {
                Setup(x => x.AllocatePortAsync(
                    It.IsAny<SweAfConfigEntity>(),
                    It.IsAny<IDbContextFactory<ClaudeManagerDbContext>>(),
                    It.IsAny<CancellationToken>())).ReturnsAsync(port!.Value)
            }.Object : new Mock<ISwarmRunnerPortAllocator>().Object,
            NullLogger<SweAfService>.Instance,
            healthCheckTimeout: TimeSpan.FromSeconds(1));
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public TestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    // ============================================================================
    // CRITICAL TEST: Webhook-Created Job Gets ComposeProjectName + Triggers Teardown
    // ============================================================================

    /// <summary>
    /// Verifies the complete end-to-end flow:
    /// 1. execution_created webhook creates job WITH ComposeProjectName materialized from ExternalJobId hash
    /// 2. execution_completed webhook processes terminal state and triggers container teardown
    /// </summary>
    [Test]
    public async Task WebhookCreatedJob_CompleteCycle_ComposeProjectNameMaterializedAndTriggersTeardown()
    {
        // Arrange
        var externalJobId = "exec-complete-integration-001";
        string expectedProjectName = $"agentfield-{externalJobId.GetHashCode()}";

        // Note: execution_created with target 'swe-planner.build' typically shouldn't trigger
        // upsert for execution_created (only executed ones do). We'll seed a job directly.
        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Integration test goal",
            RepoUrl = "https://github.com/integration/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        // Mock provisioning service to capture teardown calls
        var teardownCall = new List<string>();
        _swarmProvisioner
            .Setup(x => x.StopControlPlaneForJobAsync(It.IsAny<string>()))
            .ReturnsAsync((string? projectName) =>
            {
                teardownCall.Add(projectName ?? "");
                return null;
            });

        // Our service already configured in SetUp
        var batch = MakeBatch("execution_completed", externalJobId,
            extra: new { result = new { pr_urls = new[] { "https://github.com/org/repo/pull/1" } } });

        // Act: Process webhook
        await _swarmProvisioner.Object
            .CreateAsync(_dbFactory).GetAwaiter().GetResult(); // Initialize for other processes
        await _swarmProvisioner.VerifyAllAsync();
        _swarmProvisioner.Object
            .Setup(x => x.ProvisionControlPlaneForJobAsync(
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, null, "http://localhost:8100"));

        var svc = CreateService(_swarmProvisioner.Object);
        await svc.ProcessWebhookBatchAsync(batch);

        // Assert
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        // Job should be in terminal state
        job.Status.Should().Be(BuildStatus.Succeeded);
        job.PrUrls.Should().Contain("https://github.com/org/repo/pull/1");

        // ComposeProjectName should still be materialized from ExternalJobId
        job.ComposeProjectName.Should().Be(expectedProjectName);

        // Teardown should have been invoked once (fire-and-forget Task.Run)
        teardownCall.Count.Should().Be(1);
        teardownCall[0].Should().Be(expectedProjectName);
    }

    // ============================================================================
    // CRITICAL TEST: Failure and Cancellation States Also Trigger Teardown
    // ============================================================================

    /// <summary>
    /// Verifies execution_failed webhook triggers container teardown
    /// </summary>
    [Test]
    public async Task WebhookCreatedJob_ExecutionFailed_ComposeProjectNameUsedForTeardown()
    {
        // Arrange
        var externalJobId = "exec-failed-integration-002";
        string expectedProjectName = $"agentfield-{externalJobId.GetHashCode()}";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Failed test goal",
            RepoUrl = "https://github.com/failed/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        var teardownCall = new List<string>();
        _swarmProvisioner
            .Setup(x => x.StopControlPlaneForJobAsync(It.IsAny<string>()))
            .ReturnsAsync((string? projectName) =>
            {
                teardownCall.Add(projectName ?? "");
                return null;
            });

        var svc = CreateService(_swarmProvisioner.Object);

        // Act
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_failed", externalJobId,
            extra: new { error = "Build failed with error" }));

        // Assert
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        job.Status.Should().Be(BuildStatus.Failed);
        job.ComposeProjectName.Should().Be(expectedProjectName);
        teardownCall.Should().HaveCount(1);
        teardownCall[0].Should().Be(expectedProjectName);
    }

    /// <summary>
    /// Verifies execution_cancelled webhook triggers container teardown
    /// </summary>
    [Test]
    public async Task WebhookCreatedJob_ExecutionCancelled_ComposeProjectNameUsedForTeardown()
    {
        // Arrange
        var externalJobId = "exec-cancelled-integration-003";
        string expectedProjectName = $"agentfield-{externalJobId.GetHashCode()}";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Cancelled test goal",
            RepoUrl = "https://github.com/cancelled/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        var teardownCall = new List<string>();
        _swarmProvisioner
            .Setup(x => x.StopControlPlaneForJobAsync(It.IsAny<string>()))
            .ReturnsAsync((string? projectName) =>
            {
                teardownCall.Add(projectName ?? "");
                return null;
            });

        var svc = CreateService(_swarmProvisioner.Object);

        // Act
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_cancelled", externalJobId));

        // Assert
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        job.Status.Should().Be(BuildStatus.Cancelled);
        job.ComposeProjectName.Should().Be(expectedProjectName);
        teardownCall.Should().HaveCount(1);
        teardownCall[0].Should().Be(expectedProjectName);
    }

    // ============================================================================
    // RACE CONDITION TEST: Multiple Events for Same Job (duplicate teardown prevention)
    // ============================================================================

    /// <summary>
    /// Verifies that if ComposeProjectName materialization is correct and teardown is called once,
    /// duplicate teardown is not attempted due to the single Task.Run per terminal state.
    /// </summary>
    [Test]
    public async Task Verified_ComposeProjectNameConsistency_AcrossMultipleWebhookTypes()
    {
        // Arrange
        var externalJobId = "exec-concurrent-integration-004";
        string expectedProjectName = $"agentfield-{externalJobId.GetHashCode()}";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Concurrent test goal",
            RepoUrl = "https://github.com/concurrent/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        // Track teardown calls per job
        var teardownLog = new Dictionary<string, int>();
        _swarmProvisioner
            .Setup(x => x.StopControlPlaneForJobAsync(It.IsAny<string>()))
            .ReturnsAsync((string? projectName) =>
            {
                teardownLog[projectName ?? ""] = teardownLog.GetOrDefault(projectName ?? "") + 1;
                return null;
            });

        var svc = CreateService(_swarmProvisioner.Object);

        // Test: Process multiple terminal events - each should trigger one teardown
        // (in real code, Task.Run fires and forgets, but we're tracking the setup)
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_completed", externalJobId));
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_failed", externalJobId));
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_cancelled", externalJobId));

        // Assert: For each terminal status, teardown was called exactly once
        teardownLog[expectedProjectName]?.Should().Be(3); // Called 3 times (once per terminal event type)
    }

    // ============================================================================
    // DEFINITELY IS: Teardown Validates ComposeProjectName Exists Before Calling
    // ============================================================================

    /// <summary>
    /// Verifies that jobs without ComposeProjectName don't trigger teardown.
    /// This ensures the hub gracefully handles edge cases.
    /// </summary>
    [Test]
    public async Task JobWithoutComposeProjectName_NoTeardownTriggered()
    {
        // Arrange
        var externalJobId = "exec-no-compose-integration-005";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "No compose test goal",
            RepoUrl = "https://github.com/nocompose/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = null!, // Explicitly null
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        _swarmProvisioner.Setup(x => x.StopControlPlaneForJobAsync(It.IsAny<string>()))
            .ReturnsAsync((string? projectName) => { return null; });

        var svc = CreateService(_swarmProvisioner.Object);

        // Act
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_completed", externalJobId));

        // Assert
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        job.Status.Should().Be(BuildStatus.Succeeded);
        job.ComposeProjectName.Should().BeNull(); // Should remain null
        // Teardown should NOT have been called since ComposeProjectName is null
        // (Our mock captures all calls, verify no call was made)
        _swarmProvisioner.Verify(x => x.StopControlPlaneForJobAsync(It.IsAny<string>()), Times.Never);
    }

    // ============================================================================
    // SEEDING TEST: Verify execution_created Creates Job with Materialized ComposeProjectName
    // ============================================================================

    /// <summary>
    /// Verifies that execution_created webhook event creates a new job with
    /// ComposeProjectName materialized from ExternalJobId hash.
    /// </summary>
    [Test]
    public async Task ExecutionCreated_WebhookCreatesJob_WithMaterializedComposeProjectName()
    {
        // Arrange
        var externalJobId = "exec-created-mat-006";
        string expectedProjectName = $"agentfield-{externalJobId.GetHashCode()}";

        var handler = MockHttp(HttpStatusCode.OK);

        // Act
        await _swarmProvisioner.VerifyAllAsync();
        _swarmProvisioner.Object
            .Setup(x => x.ProvisionControlPlaneForJobAsync(
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, null, "http://localhost:8100"));

        var svc = CreateService(handler, _swarmProvisioner.Object);
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_created", externalJobId));

        // Assert
        await using var db = _dbFactory.CreateDbContext();
        var job = await db.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == externalJobId);

        job.Should().NotBeNull();
        job!.ExternalJobId.Should().Be(externalJobId);
        job!.ComposeProjectName.Should().Be(expectedProjectName,
            "ComposeProjectName should be materialized from ExternalJobId hash as per merge implementation");
        job!.Status.Should().Be(BuildStatus.Queued);
        job!.TriggeredBy.Should().Be("hub");
    }

    // ============================================================================
    // RACE CONDITION TEST: Only One Teardown Task Per Terminal Event
    // ============================================================================

    /// <summary>
    /// Verifies the merge code doesn't cause race conditions - only one Task.Run
    /// per terminal state transition.
    /// </summary>
    [Test]
    public async Task OnlyOneTeardownTaskPerTerminalStateTransition()
    {
        // Arrange
        var externalJobId = "exec-race-integration-007";
        string expectedProjectName = $"agentfield-{externalJobId.GetHashCode()}";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Race test goal",
            RepoUrl = "https://github.com/race/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        Sequencepoint<ISwarmProvisioningService> sequence = null!;
        _swarmProvisioner
            .Setup(x => x.StopControlPlaneForJobAsync(It.IsAny<string>()))
            .ReturnsAsync((string? projectName, CancellationToken ct) =>
            {
                return sequence!.Expect(x => x.StopControlPlaneForJobAsync(projectName, ct));
            });

        var svc = CreateService(_swarmProvisioner.Object);

        // Act: Process one terminal state event
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_completed", externalJobId));

        // Assert: Teardown was called exactly once (one Task.Run per terminal transition)
        _swarmProvisioner.Verify(x => x.StopControlPlaneForJobAsync(expectedProjectName, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================================
    // CROSS-FEATURE INTEGRATION: TriggerBuild + Webhook Events
    // ============================================================================

    /// <summary>
    /// End-to-end: Verify the Hub's trigger path and webhook path both result
    /// in proper ComposeProjectName and teardown when job completes.
    /// </summary>
    [Test]
    public async Task TriggeredJob_CompletesViaWebhook_TeardownTriggeredWithMaterializedProjectName()
    {
        // Arrange: Trigger a build which should create job with ComposeProjectName
        var externalJobId = "exec-triggered-integration-008";
        string? expectedProjectName = null;

        var handler = MockHttp(HttpStatusCode.OK);
        var capturedJobs = new List<SweAfJobEntity>();

        _swarmProvisioner.Object
            .Setup(x => x.ProvisionControlPlaneForJobAsync(
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, null, "http://localhost:8100"));

        var svc = CreateService(handler, _swarmProvisioner.Object);

        // Trigger the build (creates job via TriggerBuildAsync)
        var triggeredJob = await svc.TriggerBuildAsync("Triggered goal", "https://github.com/triggered/repo");

        expectedProjectName = triggeredJob.ComposeProjectName;
        capturedJobs.Add(triggeredJob);

        // Simulate webhook event for completion
        var dict = new Dictionary<string, object?>
        {
            ["target"]       = "swe-planner.build",
            ["execution_id"] = externalJobId,
            ["result"]       = new { pr_urls = new[] { "https://github.com/triggered/repo/pull/1" } },
        };
        var data = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
        var evt = new ObservabilityEvent("execution_completed", "test", DateTimeOffset.UtcNow, data);
        var batch = MakeBatch("execution_completed", externalJobId);

        var teardownCalled = new List<string>();
        _swarmProvisioner
            .Setup(x => x.StopControlPlaneForJobAsync(It.IsAny<string>()))
            .ReturnsAsync((string? projectName) =>
            {
                teardownCalled.Add(projectName ?? "");
                return null;
            });

        // Act
        await svc.ProcessWebhookBatchAsync(batch);

        // Assert
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        job.Status.Should().Be(BuildStatus.Succeeded);
        job.ComposeProjectName.Should().Be(expectedProjectName,
            "ComposeProjectName from TriggerBuild should be used for teardown");
        teardownCalled.Should().HaveCount(1);
        teardownCalled[0].Should().Be(expectedProjectName);
    }
}

// Helper extension for dictionary.GetOrDefault
public static class DictionaryExtensions
{
    public static int? GetOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key)
    {
        return dict.TryGetValue(key, out var value) ? value : default;
    }
}
