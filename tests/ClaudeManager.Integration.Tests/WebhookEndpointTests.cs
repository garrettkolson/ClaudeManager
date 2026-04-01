using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeManager.Integration.Tests;

// ── Factory variant that configures a webhook HMAC secret ─────────────────

internal sealed class WebhookSecretFactory : HubWebApplicationFactory
{
    public const string WebhookSecret = "test-webhook-secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Inject the webhook secret via configuration; SweAfConfigService falls back to
        // SweAf:WebhookSecret when no DB row is present (integration test DB starts empty).
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SweAf:WebhookSecret"] = WebhookSecret,
            }));
    }
}

internal static class WebhookTestConstants
{
    public const string WebhookSecret = WebhookSecretFactory.WebhookSecret;
}

// ── Webhook endpoint tests (no HMAC secret configured) ────────────────────

/// <summary>
/// End-to-end tests for <c>POST /api/webhooks/agentfield</c>.
/// Uses the default factory (no <c>SweAf:WebhookSecret</c>) so every request
/// passes the signature check regardless of headers.
/// </summary>
[TestFixture]
public class WebhookEndpointTests
{
    private HubWebApplicationFactory _factory = default!;
    private HttpClient _client = default!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new HubWebApplicationFactory();
        _client  = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StringContent JsonBatch(string batchJson) =>
        new(batchJson, Encoding.UTF8, "application/json");

    private static string MakeBatchJson(string eventType, string executionId,
        string? goal = null, string? repoUrl = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["target"]       = "swe-planner.build",
            ["execution_id"] = executionId,
        };
        if (goal    is not null) data["goal"]     = goal;
        if (repoUrl is not null) data["repo_url"] = repoUrl;

        var evt = new
        {
            event_type   = eventType,
            event_source = "test",
            timestamp    = DateTimeOffset.UtcNow,
            data,
        };
        var batch = new
        {
            batch_id    = "b-" + Guid.NewGuid().ToString("N")[..8],
            event_count = 1,
            events      = new[] { evt },
            timestamp   = DateTimeOffset.UtcNow,
        };
        return JsonSerializer.Serialize(batch);
    }

    private IDbContextFactory<ClaudeManagerDbContext> DbFactory =>
        _factory.Services.GetRequiredService<IDbContextFactory<ClaudeManagerDbContext>>();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Post_MalformedJson_ReturnsBadRequest()
    {
        var resp = await _client.PostAsync("/api/webhooks/agentfield",
            new StringContent("not-json-at-all", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Post_ValidBatch_NoSecretConfigured_ReturnsOk()
    {
        var json = MakeBatchJson("execution_created", $"ext-{Guid.NewGuid():N}");
        var resp = await _client.PostAsync("/api/webhooks/agentfield", JsonBatch(json));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Post_ExecutionCreated_JobUpsertedInDb()
    {
        var extId = $"ext-e2e-{Guid.NewGuid():N}";
        var json  = MakeBatchJson("execution_created", extId,
            goal: "E2E goal", repoUrl: "https://github.com/org/repo");

        var resp = await _client.PostAsync("/api/webhooks/agentfield", JsonBatch(json));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db  = DbFactory.CreateDbContext();
        var job = await db.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == extId);
        job.Should().NotBeNull();
        job!.Goal.Should().Be("E2E goal");
        job.Status.Should().Be(BuildStatus.Queued);
    }

    [Test]
    public async Task Post_ExecutionStarted_ExistingJob_StatusUpdatedToRunning()
    {
        var extId = $"ext-start-{Guid.NewGuid():N}";

        // Seed a queued job directly in the DB
        await using (var db = DbFactory.CreateDbContext())
        {
            db.SweAfJobs.Add(new SweAfJobEntity
            {
                ExternalJobId = extId,
                Goal          = "Seed goal",
                RepoUrl       = "https://github.com/org/repo",
                Status        = BuildStatus.Queued,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var json = MakeBatchJson("execution_started", extId);
        var resp = await _client.PostAsync("/api/webhooks/agentfield", JsonBatch(json));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = DbFactory.CreateDbContext();
        var job = await verify.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == extId);
        job!.Status.Should().Be(BuildStatus.Running);
        job.StartedAt.Should().NotBeNull();
    }

    [Test]
    public async Task Post_NonExecutionEvent_DoesNotUpsertJob()
    {
        var extId = $"ext-skip-{Guid.NewGuid():N}";
        var json  = MakeBatchJson("system_state_snapshot", extId);

        var resp = await _client.PostAsync("/api/webhooks/agentfield", JsonBatch(json));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = DbFactory.CreateDbContext();
        var job = await db.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == extId);
        job.Should().BeNull();
    }
}

// ── Webhook HMAC tests (secret configured) ────────────────────────────────

/// <summary>
/// End-to-end tests for <c>POST /api/webhooks/agentfield</c> HMAC signature
/// verification, using a factory that configures <c>SweAf:WebhookSecret</c>.
/// </summary>
[TestFixture]
public class WebhookHmacTests
{
    private WebhookSecretFactory _factory = default!;
    private HttpClient _client = default!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new WebhookSecretFactory();
        _client  = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] MakeBatchBytes(string extId)
    {
        var batch = new
        {
            batch_id    = "b-hmac",
            event_count = 1,
            events      = new[]
            {
                new
                {
                    event_type   = "execution_created",
                    event_source = "test",
                    timestamp    = DateTimeOffset.UtcNow,
                    data         = new Dictionary<string, object?>
                    {
                        ["target"]       = "swe-planner.build",
                        ["execution_id"] = extId,
                    },
                },
            },
            timestamp = DateTimeOffset.UtcNow,
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(batch));
    }

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private static HttpContent ToJsonContent(byte[] body) =>
        new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Post_ValidHmac_ReturnsOk()
    {
        var body    = MakeBatchBytes($"hmac-ok-{Guid.NewGuid():N}");
        var sig     = Sign(WebhookTestConstants.WebhookSecret, body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/agentfield")
        {
            Content = ToJsonContent(body),
            Headers = { { "X-AgentField-Signature", sig } },
        };
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Post_InvalidHmac_ReturnsUnauthorized()
    {
        var body    = MakeBatchBytes($"hmac-bad-{Guid.NewGuid():N}");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/agentfield")
        {
            Content = ToJsonContent(body),
            Headers = { { "X-AgentField-Signature", "sha256=deadbeef" } },
        };
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Post_MissingSignature_ReturnsUnauthorized()
    {
        var body = MakeBatchBytes($"hmac-missing-{Guid.NewGuid():N}");
        var resp = await _client.PostAsync("/api/webhooks/agentfield", ToJsonContent(body));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
