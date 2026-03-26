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

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn      = conn;
        _dbFactory = factory;
        _notifier  = new BuildNotifier();
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SweAfService CreateService(HttpMessageHandler handler, string baseUrl = "https://af.test", string apiKey = "key")
    {
        var http   = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        var config = new SweAfConfig { BaseUrl = baseUrl, ApiKey = apiKey };
        return new SweAfService(http, config, _dbFactory, _notifier, NullLogger<SweAfService>.Instance);
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

        var config = new SweAfConfig
        {
            BaseUrl = "https://af.test",
            ApiKey  = "key",
            Runtime = "claude_code",
            Models  = new SweAfModelsConfig { Default = "sonnet", Coder = "opus" },
        };
        var http = new HttpClient(handlerMock.Object) { BaseAddress = new Uri("https://af.test/") };
        var svc  = new SweAfService(http, config, _dbFactory, _notifier,
            NullLogger<SweAfService>.Instance);

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
        var handler = MockHttp(HttpStatusCode.InternalServerError);
        var svc = CreateService(handler.Object);

        await svc.Invoking(s => s.TriggerBuildAsync("Goal", "https://github.com/org/repo"))
            .Should().ThrowAsync<HttpRequestException>();
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
        ok.Should().BeFalse();
        err.Should().Contain("500");
    }

    [Test]
    public async Task CancelJobAsync_DoesNotUpdateDbStatus()
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
        refetched.Status.Should().Be(BuildStatus.Running);
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
}
