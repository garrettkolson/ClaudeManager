using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

public class SweAfService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly SweAfConfigService _configSvc;
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly BuildNotifier _notifier;
    private readonly ILogger<SweAfService> _logger;

    private static readonly JsonSerializerOptions _ignoreNulls = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool    IsConfigured  => _configSvc.IsConfigured;
    public string? WebhookSecret => _configSvc.WebhookSecret;
    public string? HubPublicUrl  => _configSvc.HubPublicUrl;

    public SweAfService(
        IHttpClientFactory httpFactory,
        SweAfConfigService configSvc,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        BuildNotifier notifier,
        ILogger<SweAfService> logger)
    {
        _httpFactory = httpFactory;
        _configSvc   = configSvc;
        _dbFactory   = dbFactory;
        _notifier    = notifier;
        _logger      = logger;
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    /// <summary>Creates a per-call HttpClient from the factory (safe for singletons).</summary>
    private HttpClient Http() => _httpFactory.CreateClient("sweaf");

    /// <summary>Builds a request with the current BaseUrl and Bearer token.</summary>
    private HttpRequestMessage BuildRequest(
        HttpMethod method, string path, SweAfConfigEntity cfg,
        HttpContent? content = null)
    {
        var baseUri = new Uri(cfg.BaseUrl.TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, path.TrimStart('/')));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", cfg.ApiKey!);
        if (content is not null)
            request.Content = content;
        return request;
    }

    // ── Retry ────────────────────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> RetryJobAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.SweAfJobs.FindAsync([jobId], ct);
        if (job is null) return (false, "Job not found.");

        try
        {
            await TriggerBuildAsync(job.Goal, job.RepoUrl);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retry failed for job {JobId}", jobId);
            return (false, ex.Message);
        }
    }

    // ── Webhook registration ─────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> RegisterWebhookAsync(
        string url, string? secret, CancellationToken ct = default)
    {
        var cfg = _configSvc.GetConfig();
        if (!cfg.IsConfigured) return (false, "SWE-AF is not configured.");

        object payload = string.IsNullOrWhiteSpace(secret)
            ? new { url }
            : new { url, secret };

        using var request = BuildRequest(
            HttpMethod.Post,
            "/api/v1/settings/observability-webhook",
            cfg,
            JsonContent.Create(payload));

        var resp = await Http().SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Webhook registration returned {Status}", (int)resp.StatusCode);
            return (false, $"AgentField returned {(int)resp.StatusCode}.");
        }

        _logger.LogInformation("Observability webhook registered: {Url}", url);
        return (true, null);
    }

    // ── Trigger ───────────────────────────────────────────────────────────────

    public async Task<SweAfJobEntity> TriggerBuildAsync(string goal, string repoUrl)
    {
        var cfg = _configSvc.GetConfig();
        if (!cfg.IsConfigured)
            throw new InvalidOperationException(
                "SWE-AF is not configured. Go to SWE-AF Servers to add the AgentField URL and API key.");

        SweAfModelsConfig? models = null;
        if (cfg.ModelDefault is not null || cfg.ModelCoder is not null || cfg.ModelQa is not null)
            models = new SweAfModelsConfig
                { Default = cfg.ModelDefault, Coder = cfg.ModelCoder, Qa = cfg.ModelQa };

        var payload = new
        {
            input = new
            {
                goal,
                repo_url = repoUrl,
                config   = new { runtime = cfg.Runtime, models },
            },
        };

        using var request = BuildRequest(
            HttpMethod.Post,
            "/api/v1/execute/async/swe-planner.build",
            cfg,
            JsonContent.Create(payload, options: _ignoreNulls));

        var resp = await Http().SendAsync(request);
        resp.EnsureSuccessStatusCode();

        var result = await resp.Content.ReadFromJsonAsync<ExecuteResponse>()
            ?? throw new InvalidOperationException("Empty response from AgentField");

        var now = DateTimeOffset.UtcNow;
        var job = new SweAfJobEntity
        {
            ExternalJobId = result.ExecutionId,
            Goal          = goal,
            RepoUrl       = repoUrl,
            Status        = BuildStatus.Queued,
            TriggeredBy   = "hub",
            CreatedAt     = now,
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SweAfJobs.Add(job);
        await db.SaveChangesAsync();

        _notifier.NotifyBuildChanged(job);
        _logger.LogInformation(
            "SWE-AF build triggered: {ExternalJobId} for {RepoUrl}", job.ExternalJobId, repoUrl);
        return job;
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    public async Task<SweAfJobEntity?> GetJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SweAfJobs.FindAsync([jobId], ct);
    }

    public async Task<BuildExecutionDetail?> FetchExecutionDetailAsync(
        string externalJobId, CancellationToken ct = default)
    {
        var cfg = _configSvc.GetConfig();
        if (!cfg.IsConfigured) return null;
        try
        {
            using var request = BuildRequest(
                HttpMethod.Get, $"/api/v1/executions/{externalJobId}", cfg);

            var resp = await Http().SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var detail = await resp.Content.ReadFromJsonAsync<ExecutionStatusResponse>(
                cancellationToken: ct);
            if (detail is null) return null;

            return new BuildExecutionDetail(
                Status:     detail.Status,
                ResultJson: detail.Result is { ValueKind: not JsonValueKind.Null }
                    ? JsonSerializer.Serialize(detail.Result,
                        new JsonSerializerOptions { WriteIndented = true })
                    : null,
                Error:      detail.Error,
                InputJson:  detail.Input is { ValueKind: not JsonValueKind.Null }
                    ? JsonSerializer.Serialize(detail.Input,
                        new JsonSerializerOptions { WriteIndented = true })
                    : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch execution detail for {Id}", externalJobId);
            return null;
        }
    }

    public async Task<List<SweAfJobEntity>> GetAllJobsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var all = await db.SweAfJobs.ToListAsync();
        return all.OrderByDescending(j => j.CreatedAt).ToList();
    }

    // ── Job control ───────────────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> CancelJobAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.SweAfJobs.FindAsync([jobId], ct);
        if (job is null) return (false, "Job not found.");

        var cfg = _configSvc.GetConfig();
        using var request = BuildRequest(
            HttpMethod.Post, $"/api/v1/executions/{job.ExternalJobId}/cancel", cfg);

        var resp = await Http().SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cancel request for {JobId} returned {Status}",
                jobId, (int)resp.StatusCode);
            return (false, $"AgentField returned {(int)resp.StatusCode}.");
        }

        _logger.LogInformation("Cancel request sent for {ExternalJobId}", job.ExternalJobId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ApproveJobAsync(
        long jobId, bool approved, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.SweAfJobs.FindAsync([jobId], ct);
        if (job is null) return (false, "Job not found.");

        var cfg     = _configSvc.GetConfig();
        var payload = new { execution_id = job.ExternalJobId, approved };
        using var request = BuildRequest(
            HttpMethod.Post, "/api/v1/webhooks/approval-response", cfg,
            JsonContent.Create(payload));

        var resp = await Http().SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Approval response for {JobId} returned {Status}",
                jobId, (int)resp.StatusCode);
            return (false, $"AgentField returned {(int)resp.StatusCode}.");
        }

        _logger.LogInformation("Approval ({Approved}) sent for {ExternalJobId}",
            approved, job.ExternalJobId);
        return (true, null);
    }

    // ── Webhook processing ────────────────────────────────────────────────────

    public async Task ProcessWebhookBatchAsync(ObservabilityBatch batch)
    {
        foreach (var evt in batch.Events)
        {
            if (!evt.EventType.StartsWith("execution_", StringComparison.Ordinal))
                continue;

            var data = evt.Data;

            if (!data.TryGetProperty("target", out var targetProp) ||
                targetProp.GetString() != "swe-planner.build")
                continue;

            if (!data.TryGetProperty("execution_id", out var idProp))
                continue;

            var externalJobId = idProp.GetString();
            if (string.IsNullOrEmpty(externalJobId)) continue;

            try
            {
                await ApplyEventAsync(externalJobId, evt.EventType, data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply webhook event {EventType} for {JobId}",
                    evt.EventType, externalJobId);
            }
        }
    }

    private async Task ApplyEventAsync(string externalJobId, string eventType, JsonElement data)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var job = await db.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == externalJobId);

        if (job is null)
        {
            if (eventType is not ("execution_created" or "execution_started"))
                return;

            job = new SweAfJobEntity
            {
                ExternalJobId = externalJobId,
                Goal          = data.TryGetProperty("goal",     out var g) ? g.GetString() ?? "" : "",
                RepoUrl       = data.TryGetProperty("repo_url", out var r) ? r.GetString() ?? "" : "",
                Status        = BuildStatus.Queued,
                CreatedAt     = DateTimeOffset.UtcNow,
            };
            db.SweAfJobs.Add(job);
        }

        var now = DateTimeOffset.UtcNow;

        switch (eventType)
        {
            case "execution_created":
                job.Status = BuildStatus.Queued;
                break;

            case "execution_started":
                job.Status    = BuildStatus.Running;
                job.StartedAt ??= now;
                break;

            case "execution_updated":
                if (data.TryGetProperty("status", out var updStatus))
                    job.Status = ParseStatus(updStatus.GetString());
                break;

            case "execution_completed":
                job.Status      = BuildStatus.Succeeded;
                job.CompletedAt = now;
                job.PrUrls      = ExtractPrUrls(data);
                break;

            case "execution_failed":
                job.Status       = BuildStatus.Failed;
                job.CompletedAt  = now;
                job.ErrorMessage = ExtractErrorMessage(data);
                break;

            case "execution_cancelled":
                job.Status      = BuildStatus.Cancelled;
                job.CompletedAt = now;
                break;

            case "execution_waiting":
            case "execution_paused":
                job.Status = BuildStatus.Waiting;
                break;
        }

        await db.SaveChangesAsync();
        _notifier.NotifyBuildChanged(job);
    }

    // ── Startup reconciliation ────────────────────────────────────────────────

    public async Task ReconcileAsync(IEnumerable<string> externalJobIds, CancellationToken ct = default)
    {
        var cfg = _configSvc.GetConfig();
        foreach (var id in externalJobIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = BuildRequest(HttpMethod.Get, $"/api/v1/executions/{id}", cfg);
                var resp = await Http().SendAsync(request, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var status = await resp.Content.ReadFromJsonAsync<ExecutionStatusResponse>(
                    cancellationToken: ct);
                if (status is null) continue;

                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var job = await db.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == id, ct);
                if (job is null) continue;

                var prev   = job.Status;
                job.Status = ParseStatus(status.Status);

                if (job.Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Cancelled)
                    job.CompletedAt ??= DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                if (job.Status != prev)
                    _notifier.NotifyBuildChanged(job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to reconcile SWE-AF execution {Id}", id);
            }
        }
    }

    // ── HMAC signature verification ───────────────────────────────────────────

    public static bool VerifySignature(string? secret, byte[] body, string? signature)
    {
        if (string.IsNullOrEmpty(secret)) return true;
        if (string.IsNullOrEmpty(signature)) return false;

        var hex = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature[7..]
            : signature;

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var computed   = hmac.ComputeHash(body);
            var expected   = Convert.FromHexString(hex);
            return CryptographicOperations.FixedTimeEquals(computed, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BuildStatus ParseStatus(string? s) => s switch
    {
        "queued"              => BuildStatus.Queued,
        "running"             => BuildStatus.Running,
        "waiting" or "paused" => BuildStatus.Waiting,
        "succeeded"           => BuildStatus.Succeeded,
        "failed"              => BuildStatus.Failed,
        "cancelled"           => BuildStatus.Cancelled,
        _                     => BuildStatus.Running,
    };

    private static string? ExtractPrUrls(JsonElement data)
    {
        if (!data.TryGetProperty("result", out var result)) return null;
        if (result.TryGetProperty("pr_urls", out var arr))
            return arr.GetRawText();
        if (result.TryGetProperty("pull_request_url", out var single))
            return $"[\"{single.GetString()}\"]";
        return null;
    }

    private static string? ExtractErrorMessage(JsonElement data)
    {
        if (data.TryGetProperty("error",         out var e)) return e.GetString();
        if (data.TryGetProperty("error_message", out var m)) return m.GetString();
        return null;
    }
}
