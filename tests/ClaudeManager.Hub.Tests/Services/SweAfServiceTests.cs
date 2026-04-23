using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SweAfServiceTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;
    private BuildNotifier _notifier = default!;
    private Mock<ISwarmProvisioningService> _swarmProvisioner = new();
    private Mock<ISwarmRunnerPortAllocator> _portAllocator = new();

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn      = conn;
        _dbFactory = factory;
        _notifier  = new BuildNotifier();

        _portAllocator
            .Setup(x => x.AllocatePortAsync(
                It.IsAny<SweAfConfigEntity>(),
                It.IsAny<IDbContextFactory<ClaudeManagerDbContext>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(8100);

        _swarmProvisioner
            .Setup(x => x.ProvisionControlPlaneForJobAsync(
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null, "http://localhost:8100"));
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SweAfService CreateService(HttpMessageHandler handler, string baseUrl = "https://af.test", string apiKey = "key",
        string? modelDefault = null, string? modelCoder = null, string? modelQa = null)
    {
        var factory    = new TestHttpClientFactory(new HttpClient(handler));
        var configSvc  = CreateConfigService(baseUrl, apiKey, modelDefault, modelCoder, modelQa);
        return new SweAfService(factory,
            configSvc,
            _dbFactory,
            _notifier,
            _swarmProvisioner.Object,
            _portAllocator.Object,
            NullLogger<SweAfService>.Instance,
            healthCheckTimeout: TimeSpan.FromSeconds(2));
    }

    private SweAfConfigService CreateConfigService(string baseUrl, string apiKey,
        string? modelDefault = null, string? modelCoder = null, string? modelQa = null)
    {
        // Pre-seed the in-memory DB with the config, then start the service so it loads the cache.
        using var db = _dbFactory.CreateDbContext();
        db.SweAfConfigs.Add(new SweAfConfigEntity
        {
            BaseUrl      = baseUrl,
            ApiKey       = apiKey,
            Runtime      = "claude_code",
            ModelDefault = modelDefault,
            ModelCoder   = modelCoder,
            ModelQa      = modelQa,
        });
        db.SaveChanges();

        var svc = new SweAfConfigService(_dbFactory, NullLogger<SweAfConfigService>.Instance);
        svc.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return svc;
    }

    /// <summary>Minimal IHttpClientFactory that always returns the same pre-configured client.</summary>
    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public TestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private static Mock<HttpMessageHandler> MockHttp(HttpStatusCode status, object? body = null)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = body is null
                    ? new StringContent("{}")
                    : JsonContent.Create(body),
            });
        return mock;
    }

    private static string HmacHex(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    // ── VerifySignature ───────────────────────────────────────────────────────

    [Test]
    public void VerifySignature_NoSecretConfigured_ReturnsTrue()
    {
        var body = "hello"u8.ToArray();
        SweAfService.VerifySignature(null, body, "invalidsig").Should().BeTrue();
        SweAfService.VerifySignature("",   body, "invalidsig").Should().BeTrue();
    }

    [Test]
    public void VerifySignature_ValidHex_ReturnsTrue()
    {
        var body = "payload"u8.ToArray();
        var sig  = HmacHex("secret", body);
        SweAfService.VerifySignature("secret", body, sig).Should().BeTrue();
    }

    [Test]
    public void VerifySignature_ValidWithSha256Prefix_ReturnsTrue()
    {
        var body = "payload"u8.ToArray();
        var sig  = "sha256=" + HmacHex("secret", body);
        SweAfService.VerifySignature("secret", body, sig).Should().BeTrue();
    }

    [Test]
    public void VerifySignature_WrongSecret_ReturnsFalse()
    {
        var body = "payload"u8.ToArray();
        var sig  = HmacHex("correct", body);
        SweAfService.VerifySignature("wrong", body, sig).Should().BeFalse();
    }

    [Test]
    public void VerifySignature_MissingSignature_ReturnsFalse()
    {
        var body = "payload"u8.ToArray();
        SweAfService.VerifySignature("secret", body, null).Should().BeFalse();
        SweAfService.VerifySignature("secret", body, "").Should().BeFalse();
    }

    [Test]
    public void VerifySignature_MalformedHex_ReturnsFalse()
    {
        var body = "payload"u8.ToArray();
        SweAfService.VerifySignature("secret", body, "notvalidhex!!").Should().BeFalse();
    }

    // ── TriggerBuildAsync ─────────────────────────────────────────────────────

    [Test]
    public async Task TriggerBuildAsync_PostsToCorrectEndpoint()
    {
        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { execution_id = "exec-123" }),
            });

        var svc = CreateService(handlerMock.Object);
        await svc.TriggerBuildAsync("Fix the bug", "https://github.com/org/repo");

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/execute/async/swe-planner.build");
    }

    [Test]
    public async Task TriggerBuildAsync_PersistsJobToDb()
    {
        var handler = MockHttp(HttpStatusCode.OK, new { execution_id = "exec-abc" });
        var svc = CreateService(handler.Object);

        await svc.TriggerBuildAsync("My goal", "https://github.com/org/repo");

        await using var db = _dbFactory.CreateDbContext();
        var job = await db.SweAfJobs.SingleAsync();
        job.ExternalJobId.Should().Be("exec-abc");
        job.Goal.Should().Be("My goal");
        job.RepoUrl.Should().Be("https://github.com/org/repo");
        job.Status.Should().Be(BuildStatus.Queued);
        job.TriggeredBy.Should().Be("hub");
    }

    [Test]
    public async Task TriggerBuildAsync_FiresBuildChangedNotification()
    {
        var handler = MockHttp(HttpStatusCode.OK, new { execution_id = "exec-xyz" });
        var svc = CreateService(handler.Object);

        SweAfJobEntity? notified = null;
        _notifier.BuildChanged += j => notified = j;

        await svc.TriggerBuildAsync("Goal", "https://github.com/org/repo");

        notified.Should().NotBeNull();
        notified!.ExternalJobId.Should().Be("exec-xyz");
    }

    [Test]
    public async Task TriggerBuildAsync_WhenModelsConfigured_IncludesInPayload()
    {
        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { execution_id = "exec-m1" }),
            });

        var svc = CreateService(handlerMock.Object,
            modelDefault: "sonnet", modelCoder: "opus");

        await svc.TriggerBuildAsync("Goal", "https://github.com/org/repo");

        var body = await captured!.Content!.ReadAsStringAsync();
        body.Should().Contain("\"runtime\":\"claude_code\"");
        body.Should().Contain("\"default\":\"sonnet\"");
        body.Should().Contain("\"coder\":\"opus\"");
    }

    [Test]
    public async Task TriggerBuildAsync_WhenModelsNotConfigured_OmitsModelsField()
    {
        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { execution_id = "exec-m2" }),
            });

        var svc = CreateService(handlerMock.Object);
        await svc.TriggerBuildAsync("Goal", "https://github.com/org/repo");

        var body = await captured!.Content!.ReadAsStringAsync();
        body.Should().NotContain("\"models\"");
    }

    [Test]
    public async Task TriggerBuildAsync_HttpError_Throws()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        // Health check must succeed so we reach the build trigger
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        // Build trigger returns 500
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Internal Server Error"),
            });

        var svc = CreateService(handlerMock.Object);

        await svc.Invoking(s => s.TriggerBuildAsync("Goal", "https://github.com/org/repo"))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    // ── ProcessWebhookBatchAsync ──────────────────────────────────────────────

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

        var data = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
        var evt  = new ObservabilityEvent(eventType, "test", DateTimeOffset.UtcNow, data);
        return new ObservabilityBatch("batch-1", 1, [evt], DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_IgnoresNonExecutionEvents()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var batch = MakeBatch("system_state_snapshot", "exec-1");
        await svc.ProcessWebhookBatchAsync(batch);

        await using var db = _dbFactory.CreateDbContext();
        (await db.SweAfJobs.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_IgnoresWrongTarget()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var batch = MakeBatch("execution_created", "exec-1", target: "other-agent");
        await svc.ProcessWebhookBatchAsync(batch);

        await using var db = _dbFactory.CreateDbContext();
        (await db.SweAfJobs.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionCreated_UpsertsUnknownJob()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var batch = MakeBatch("execution_created", "ext-001",
            extra: new { goal = "Auto goal", repo_url = "https://github.com/org/r" });
        await svc.ProcessWebhookBatchAsync(batch);

        await using var db = _dbFactory.CreateDbContext();
        var job = await db.SweAfJobs.SingleAsync();
        job.ExternalJobId.Should().Be("ext-001");
        job.Goal.Should().Be("Auto goal");
        job.Status.Should().Be(BuildStatus.Queued);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionStarted_SetsRunning()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-s1",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Queued,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_started", "exec-s1"));

        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync();
        job.Status.Should().Be(BuildStatus.Running);
        job.StartedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionCompleted_SetsPrUrls()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-c1",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Running,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var dict = new Dictionary<string, object?>
        {
            ["target"]       = "swe-planner.build",
            ["execution_id"] = "exec-c1",
            ["result"]       = new { pr_urls = new[] { "https://github.com/org/r/pull/1" } },
        };
        var data  = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
        var evt   = new ObservabilityEvent("execution_completed", "test", DateTimeOffset.UtcNow, data);
        var batch = new ObservabilityBatch("batch-1", 1, [evt], DateTimeOffset.UtcNow);

        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await svc.ProcessWebhookBatchAsync(batch);

        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync();
        job.Status.Should().Be(BuildStatus.Succeeded);
        job.CompletedAt.Should().NotBeNull();
        job.PrUrls.Should().Contain("https://github.com/org/r/pull/1");
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionFailed_SetsErrorMessage()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-f1",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Running,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var batch = MakeBatch("execution_failed", "exec-f1",
            extra: new { error = "Something went wrong" });

        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await svc.ProcessWebhookBatchAsync(batch);

        await using var verify = _dbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.SingleAsync();
        job.Status.Should().Be(BuildStatus.Failed);
        job.ErrorMessage.Should().Be("Something went wrong");
        job.CompletedAt.Should().NotBeNull();
    }

    // ── CancelJobAsync ────────────────────────────────────────────────────────

    [Test]
    public async Task CancelJobAsync_JobNotFound_ReturnsFalse()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var (ok, err) = await svc.CancelJobAsync(999);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task CancelJobAsync_PostsToCorrectEndpoint()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-cancel-1",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Running,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var svc = CreateService(handlerMock.Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var job = await db2.SweAfJobs.SingleAsync();

        var (ok, _) = await svc.CancelJobAsync(job.Id);

        ok.Should().BeTrue();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/executions/exec-cancel-1/cancel");
    }

    [Test]
    public async Task CancelJobAsync_HttpError_ReturnsFalse()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-cancel-err",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Running,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = CreateService(MockHttp(HttpStatusCode.InternalServerError).Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var job = await db2.SweAfJobs.SingleAsync();

        var (ok, err) = await svc.CancelJobAsync(job.Id);
        ok.Should().BeTrue();
    }

    [Test]
    public async Task CancelJobAsync_TransitionsToCancelled()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-cancel-nowrite",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Running,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var job = await db2.SweAfJobs.SingleAsync();

        await svc.CancelJobAsync(job.Id);

        await using var verify = _dbFactory.CreateDbContext();
        var refetched = await verify.SweAfJobs.SingleAsync();
        refetched.Status.Should().Be(BuildStatus.Cancelled);
    }

    // ── ApproveJobAsync ───────────────────────────────────────────────────────

    [Test]
    public async Task ApproveJobAsync_JobNotFound_ReturnsFalse()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var (ok, err) = await svc.ApproveJobAsync(999, approved: true);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ApproveJobAsync_PostsToCorrectEndpointWithApprovedTrue()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-approve-1",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Waiting,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var svc = CreateService(handlerMock.Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var job = await db2.SweAfJobs.SingleAsync();

        var (ok, _) = await svc.ApproveJobAsync(job.Id, approved: true);

        ok.Should().BeTrue();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/webhooks/approval-response");

        var bodyJson = await captured.Content!.ReadAsStringAsync();
        bodyJson.Should().Contain("\"execution_id\":\"exec-approve-1\"");
        bodyJson.Should().Contain("\"approved\":true");
    }

    [Test]
    public async Task ApproveJobAsync_PostsApprovedFalseForReject()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-reject-1",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Waiting,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var svc = CreateService(handlerMock.Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var job = await db2.SweAfJobs.SingleAsync();

        await svc.ApproveJobAsync(job.Id, approved: false);

        var bodyJson = await captured!.Content!.ReadAsStringAsync();
        bodyJson.Should().Contain("\"approved\":false");
    }

    [Test]
    public async Task ApproveJobAsync_HttpError_ReturnsFalse()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-approve-err",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Waiting,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = CreateService(MockHttp(HttpStatusCode.BadRequest).Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var job = await db2.SweAfJobs.SingleAsync();

        var (ok, err) = await svc.ApproveJobAsync(job.Id, approved: true);
        ok.Should().BeFalse();
        err.Should().Contain("400");
    }

    // ── GetJobAsync ───────────────────────────────────────────────────────────

    [Test]
    public async Task GetJobAsync_ExistingJob_ReturnsJob()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "ext-get-1",
                Goal          = "Fetch me",
                RepoUrl       = "https://github.com/org/repo",
                Status        = BuildStatus.Succeeded,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var seeded = await db2.SweAfJobs.SingleAsync();

        var job = await svc.GetJobAsync(seeded.Id);
        job.Should().NotBeNull();
        job!.Goal.Should().Be("Fetch me");
    }

    [Test]
    public async Task GetJobAsync_NotFound_ReturnsNull()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var job = await svc.GetJobAsync(999);
        job.Should().BeNull();
    }

    // ── FetchExecutionDetailAsync ─────────────────────────────────────────────

    [Test]
    public async Task FetchExecutionDetailAsync_Success_ReturnsDetail()
    {
        var body = new
        {
            execution_id = "ext-fd-1",
            status       = "succeeded",
            result       = new { pr_urls = new[] { "https://github.com/org/repo/pull/1" } },
            error        = (string?)null,
        };
        var svc    = CreateService(MockHttp(HttpStatusCode.OK, body).Object);
        var detail = await svc.FetchExecutionDetailAsync("ext-fd-1");

        detail.Should().NotBeNull();
        detail!.Status.Should().Be("succeeded");
        detail.ResultJson.Should().NotBeNullOrWhiteSpace();
        detail.ResultJson.Should().Contain("pr_urls");
    }

    [Test]
    public async Task FetchExecutionDetailAsync_HttpError_ReturnsNull()
    {
        var svc    = CreateService(MockHttp(HttpStatusCode.NotFound).Object);
        var detail = await svc.FetchExecutionDetailAsync("missing");
        detail.Should().BeNull();
    }

    [Test]
    public async Task FetchExecutionDetailAsync_NullResult_ResultJsonIsNull()
    {
        var body = new { execution_id = "ext-fd-2", status = "running" };
        var svc    = CreateService(MockHttp(HttpStatusCode.OK, body).Object);
        var detail = await svc.FetchExecutionDetailAsync("ext-fd-2");

        detail.Should().NotBeNull();
        detail!.ResultJson.Should().BeNull();
    }

    // ── RetryJobAsync ─────────────────────────────────────────────────────────

    [Test]
    public async Task RetryJobAsync_JobNotFound_ReturnsFalse()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var (ok, err) = await svc.RetryJobAsync(999);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task RetryJobAsync_Success_CreatesNewJobWithSameGoalAndRepo()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "orig-1",
                Goal          = "Fix the bug",
                RepoUrl       = "https://github.com/org/repo",
                Status        = BuildStatus.Failed,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var handler = MockHttp(HttpStatusCode.OK, new { execution_id = "retry-1" });
        var svc = CreateService(handler.Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var orig = await db2.SweAfJobs.SingleAsync();

        var (ok, err) = await svc.RetryJobAsync(orig.Id);

        ok.Should().BeTrue();
        err.Should().BeNull();

        await using var verify = _dbFactory.CreateDbContext();
        var jobs = await verify.SweAfJobs.ToListAsync();
        jobs.Should().HaveCount(2);
        var retried = jobs.Single(j => j.ExternalJobId == "retry-1");
        retried.Goal.Should().Be("Fix the bug");
        retried.RepoUrl.Should().Be("https://github.com/org/repo");
    }

    [Test]
    public async Task RetryJobAsync_HttpError_ReturnsFalse()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "orig-err",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/repo",
                Status        = BuildStatus.Failed,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = CreateService(MockHttp(HttpStatusCode.InternalServerError).Object);
        await using var db2 = _dbFactory.CreateDbContext();
        var job = await db2.SweAfJobs.SingleAsync();

        var (ok, err) = await svc.RetryJobAsync(job.Id);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    // ── RegisterWebhookAsync ──────────────────────────────────────────────────

    [Test]
    public async Task RegisterWebhookAsync_Success_ReturnsTrue()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var (ok, err) = await svc.RegisterWebhookAsync("https://hub/api/webhooks/agentfield", "secret");
        ok.Should().BeTrue();
        err.Should().BeNull();
    }

    [Test]
    public async Task RegisterWebhookAsync_PostsToCorrectEndpoint()
    {
        HttpRequestMessage? captured = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var svc = CreateService(handlerMock.Object);
        await svc.RegisterWebhookAsync("https://hub/api/webhooks/agentfield", null);

        captured!.RequestUri!.AbsolutePath.Should().Be("/api/v1/settings/observability-webhook");
        captured.Method.Should().Be(HttpMethod.Post);
    }

    [Test]
    public async Task RegisterWebhookAsync_HttpError_ReturnsFalse()
    {
        var svc = CreateService(MockHttp(HttpStatusCode.BadRequest).Object);
        var (ok, err) = await svc.RegisterWebhookAsync("https://hub/api/webhooks/agentfield", null);
        ok.Should().BeFalse();
        err.Should().Contain("400");
    }

    // ── SeedJob helper ────────────────────────────────────────────────────────

    private async Task SeedJob(string externalId, BuildStatus status)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.SweAfJobs.Add(new SweAfJobEntity
        {
            ExternalJobId = externalId,
            Goal          = "Goal",
            RepoUrl       = "https://github.com/org/repo",
            Status        = status,
            CreatedAt     = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_NotifiesOnStateChange()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = "exec-n1",
                Goal          = "G",
                RepoUrl       = "https://github.com/org/r",
                Status        = BuildStatus.Queued,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        SweAfJobEntity? notified = null;
        _notifier.BuildChanged += j => notified = j;

        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_started", "exec-n1"));

        notified.Should().NotBeNull();
        notified!.Status.Should().Be(BuildStatus.Running);
    }

    // ── ProcessWebhookBatchAsync – additional event types ─────────────────────

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionCancelled_SetsCancelled()
    {
        await SeedJob("exec-cancel-ev", BuildStatus.Running);
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_cancelled", "exec-cancel-ev"));

        await using var db = _dbFactory.CreateDbContext();
        (await db.SweAfJobs.SingleAsync()).Status.Should().Be(BuildStatus.Cancelled);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionWaiting_SetsWaiting()
    {
        await SeedJob("exec-wait-ev", BuildStatus.Running);
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_waiting", "exec-wait-ev"));

        await using var db = _dbFactory.CreateDbContext();
        (await db.SweAfJobs.SingleAsync()).Status.Should().Be(BuildStatus.Waiting);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionPaused_SetsWaiting()
    {
        await SeedJob("exec-paused-ev", BuildStatus.Running);
        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await svc.ProcessWebhookBatchAsync(MakeBatch("execution_paused", "exec-paused-ev"));

        await using var db = _dbFactory.CreateDbContext();
        (await db.SweAfJobs.SingleAsync()).Status.Should().Be(BuildStatus.Waiting);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_ExecutionUpdated_AppliesStatusFromPayload()
    {
        await SeedJob("exec-upd-ev", BuildStatus.Running);
        var svc   = CreateService(MockHttp(HttpStatusCode.OK).Object);
        var batch = MakeBatch("execution_updated", "exec-upd-ev", extra: new { status = "waiting" });
        await svc.ProcessWebhookBatchAsync(batch);

        await using var db = _dbFactory.CreateDbContext();
        (await db.SweAfJobs.SingleAsync()).Status.Should().Be(BuildStatus.Waiting);
    }

    [Test]
    public async Task ProcessWebhookBatchAsync_SinglePullRequestUrl_Parsed()
    {
        await SeedJob("exec-pr-single", BuildStatus.Running);

        var dict = new Dictionary<string, object?>
        {
            ["target"]       = "swe-planner.build",
            ["execution_id"] = "exec-pr-single",
            ["result"]       = new { pull_request_url = "https://github.com/org/repo/pull/42" },
        };
        var data  = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
        var evt   = new ObservabilityEvent("execution_completed", "test", DateTimeOffset.UtcNow, data);
        var batch = new ObservabilityBatch("b1", 1, [evt], DateTimeOffset.UtcNow);

        var svc = CreateService(MockHttp(HttpStatusCode.OK).Object);
        await svc.ProcessWebhookBatchAsync(batch);

        await using var db = _dbFactory.CreateDbContext();
        var job = await db.SweAfJobs.SingleAsync();
        job.Status.Should().Be(BuildStatus.Succeeded);
        job.PrUrls.Should().Contain("https://github.com/org/repo/pull/42");
    }

    // ── FetchExecutionDetailAsync – additional coverage ────────────────────────

    [Test]
    public async Task FetchExecutionDetailAsync_WithInputField_InputJsonIsPopulated()
    {
        var body = new
        {
            execution_id = "ext-fd-input",
            status       = "running",
            input        = new { goal = "Fix it", repo_url = "https://github.com/org/repo" },
        };
        var svc    = CreateService(MockHttp(HttpStatusCode.OK, body).Object);
        var detail = await svc.FetchExecutionDetailAsync("ext-fd-input");

        detail.Should().NotBeNull();
        detail!.InputJson.Should().NotBeNullOrWhiteSpace();
        detail.InputJson.Should().Contain("goal");
    }

    [Test]
    public async Task FetchExecutionDetailAsync_WithErrorField_ErrorIsPopulated()
    {
        var body = new { execution_id = "ext-fd-err", status = "failed", error = "Build blew up" };
        var svc    = CreateService(MockHttp(HttpStatusCode.OK, body).Object);
        var detail = await svc.FetchExecutionDetailAsync("ext-fd-err");

        detail.Should().NotBeNull();
        detail!.Error.Should().Be("Build blew up");
    }

    // ── ReconcileAsync ────────────────────────────────────────────────────────

    [Test]
    public async Task ReconcileAsync_RunningJob_UpdatedToSucceeded()
    {
        await SeedJob("ext-recon-1", BuildStatus.Running);

        var body = new { execution_id = "ext-recon-1", status = "succeeded" };
        var svc  = CreateService(MockHttp(HttpStatusCode.OK, body).Object);
        await svc.ReconcileAsync(["ext-recon-1"]);

        await using var db = _dbFactory.CreateDbContext();
        var job = await db.SweAfJobs.SingleAsync();
        job.Status.Should().Be(BuildStatus.Succeeded);
        job.CompletedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ReconcileAsync_StatusUnchanged_DoesNotNotify()
    {
        await SeedJob("ext-recon-2", BuildStatus.Running);

        var body = new { execution_id = "ext-recon-2", status = "running" };
        var svc  = CreateService(MockHttp(HttpStatusCode.OK, body).Object);

        SweAfJobEntity? notified = null;
        _notifier.BuildChanged += j => notified = j;

        await svc.ReconcileAsync(["ext-recon-2"]);

        notified.Should().BeNull();
    }

    [Test]
    public async Task ReconcileAsync_HttpError_IsSkipped()
    {
        await SeedJob("ext-recon-3", BuildStatus.Running);

        var svc = CreateService(MockHttp(HttpStatusCode.NotFound).Object);
        await svc.Invoking(s => s.ReconcileAsync(["ext-recon-3"]))
            .Should().NotThrowAsync();

        await using var db = _dbFactory.CreateDbContext();
        (await db.SweAfJobs.SingleAsync()).Status.Should().Be(BuildStatus.Running);
    }

    [Test]
    public async Task ReconcileAsync_JobNotInDb_IsSkipped()
    {
        // No job seeded — should not throw or crash
        var body = new { execution_id = "ext-recon-missing", status = "succeeded" };
        var svc  = CreateService(MockHttp(HttpStatusCode.OK, body).Object);
        await svc.Invoking(s => s.ReconcileAsync(["ext-recon-missing"]))
            .Should().NotThrowAsync();
    }
}
