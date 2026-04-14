using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using Moq;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SweAfServiceWebhookCreateTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private BuildNotifier _notifier = default!;
    private Mock<ISwarmProvisioningService> _swarmProvisioner = new();

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync(DataTestHelper.GetConcreteDbOptions());
        _conn      = conn as SqliteConnection;
        _dbFactory = factory;
        _conn?.Dispose();
        _notifier  = new BuildNotifier();
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

        var data = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict), new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var evt  = new ObservabilityEvent(eventType, "test", DateTimeOffset.UtcNow, data);
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

    private SweAfService CreateService(HttpMessageHandler? handler = null, ISwarmProvisioningService? provisioner = null)
    {
        var httpFactory = new TestHttpClientFactory(handler ?? MockHttp(HttpStatusCode.OK).Object);
        var configSvc = CreateConfigService("https://test.com", "testkey");
        return new SweAfService(httpFactory,
            configSvc,
            _dbFactory,
            _notifier,
            provisioner ?? _swarmProvisioner.Object,
            new Mock<ISwarmRunnerPortAllocator>().Object,
            NullLogger<SweAfService>.Instance,
            healthCheckTimeout: TimeSpan.FromSeconds(1));
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public TestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    // ── ComposeProjectName Materialization for Webhook-Created Jobs ─────────────

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionCreated_SetsComposeProjectNameFromExternalJobId()
    {
        // Arrange
        var externalJobId = "exec-test-001";
        var expectedProjectName = $"agentfield-{externalJobId.GetHashCode()}";

        var handler = MockHttp(HttpStatusCode.OK);
        var observedEvent = new ObservabilityEvent(
            "execution_created",
            "test-source",
            DateTimeOffset.UtcNow,
            new JsonElement(JsonDocument.Parse(
                $"{{\"target\":\"swe-planner.build\",\"execution_id\":\"{externalJobId}\",\"goal\":\"Test goal\",\"repo_url\":\"https://github.com/org/repo\"}}",
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })));

        var svc = CreateService(handler);
        var batch = MakeBatch("execution_created", externalJobId);

        // Act: Process execution_created webhook that will create a new job
        await svc.ProcessWebhookBatchAsync(batch);

        // Assert: Job was created with ComposeProjectName materialized from ExternalJobId hash
        await using var db = _dbFactory.CreateDbContext();
        var job = await db.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == externalJobId);

        job.Should().NotBeNull();
        job!.ExternalJobId.Should().Be(externalJobId);
        job!.ComposeProjectName.Should().Be(expectedProjectName);
        job!.Goal.Should().Be("Test goal");
        job!.RepoUrl.Should().Be("https://github.com/org/repo");
        job!.Status.Should().Be(BuildStatus.Queued);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionStarted_AlreadyHasComposeProjectName()
    {
        // Arrange
        var externalJobId = "exec-start-test";
        var hash = externalJobId.GetHashCode();
        var expectedProjectName = $"agentfield-{hash}";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Start test",
            RepoUrl = "https://github.com/org/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        var handler = MockHttp(HttpStatusCode.OK);
        var svc = CreateService(handler);

        // Act: Process execution_started for existing job (should preserve ComposeProjectName)
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_started", externalJobId));

        // Assert: ComposeProjectName is preserved and status updated
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        job.Status.Should().Be(BuildStatus.Running);
        job.StartedAt.Should().NotBeNull();
        job.ComposeProjectName.Should().Be(expectedProjectName);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionCompleted_WithPrepopulatedComposeProjectName_TriggersTeardown()
    {
        // Arrange
        var externalJobId = "exec-complete-test";
        var hash = externalJobId.GetHashCode();
        var expectedProjectName = $"agentfield-{hash}";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Complete test",
            RepoUrl = "https://github.com/org/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        // Mock provisioning service to track teardown calls
        var provisionerMock = new Mock<ISwarmProvisioningService>();
        provisionerMock.Setup(x => x.StopControlPlaneForJobAsync(expectedProjectName))
            .ReturnsAsync((string?)null);

        var dict = new Dictionary<string, object?>
        {
            ["target"]       = "swe-planner.build",
            ["execution_id"] = externalJobId,
            ["result"]       = new { pr_urls = new[] { "https://github.com/org/repo/pull/1" } },
        };
        var data  = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
        var evt   = new ObservabilityEvent("execution_completed", "test", DateTimeOffset.UtcNow, data);
        var batch = MakeBatch("execution_completed", externalJobId);

        var svc = CreateService(provisionerMock.Object);

        // Act: Process execution_completed
        await svc.ProcessWebhookBatchAsync(batch);

        // Assert: Status updated and ComposeProjectName preserved
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        job.Status.Should().Be(BuildStatus.Succeeded);
        job.PrUrls.Should().Contain("https://github.com/org/repo/pull/1");
        job.ComposeProjectName.Should().Be(expectedProjectName);
        provisionerMock.Verify(x => x.StopControlPlaneForJobAsync(expectedProjectName), Times.Once);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionFailed_WithComposeProjectName_TriggersTeardown()
    {
        // Arrange
        var externalJobId = "exec-failed-test";
        var hash = externalJobId.GetHashCode();
        var expectedProjectName = $"agentfield-{hash}";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Failed test",
            RepoUrl = "https://github.com/org/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        var provisionerMock = new Mock<ISwarmProvisioningService>();

        // Act
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_failed", externalJobId,
            extra: new { error = "Build failed" }));

        // Assert
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        job.Status.Should().Be(BuildStatus.Failed);
        job.ErrorMessage.Should().Be("Build failed");
        provisionerMock.Verify(x => x.StopControlPlaneForJobAsync(expectedProjectName), Times.Once);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionCancelled_WithComposeProjectName_TriggersTeardown()
    {
        // Arrange
        var externalJobId = "exec-cancel-test";
        var hash = externalJobId.GetHashCode();
        var expectedProjectName = $"agentfield-{hash}";

        await using var db = _dbFactory.CreateDbContext();
        var initialJob = new SweAfJobEntity
        {
            ExternalJobId = externalJobId,
            Goal = "Cancelled test",
            RepoUrl = "https://github.com/org/repo",
            Status = BuildStatus.Queued,
            ComposeProjectName = expectedProjectName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SweAfJobs.Add(initialJob);
        await db.SaveChangesAsync();

        var provisionerMock = new Mock<ISwarmProvisioningService>();
        var batch = MakeBatch("execution_cancelled", externalJobId);

        var svc = CreateService(provisionerMock.Object);
        await svc.ProcessWebhookBatchAsync(batch);

        // Assert
        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync(j => j.ExternalJobId == externalJobId);

        job.Status.Should().Be(BuildStatus.Cancelled);
        provisionerMock.Verify(x => x.StopControlPlaneForJobAsync(expectedProjectName), Times.Once);
    }
}

public static class DataTestHelper
{
    public static IDataOptions GetConcreteDbOptions()
        => new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseSqlite("::memory:")
            .Build()
            .Options;
}
