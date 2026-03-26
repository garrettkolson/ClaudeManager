using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

public class SweAfService
{
    private readonly HttpClient   _http;
    private readonly SweAfConfig  _config;
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly BuildNotifier _notifier;
    private readonly ILogger<SweAfService> _logger;

    public bool    IsConfigured  => _config.IsConfigured;
    public string? WebhookSecret => _config.WebhookSecret;
    public string? HubPublicUrl  => _config.HubPublicUrl;

    public SweAfService(
        HttpClient http,
        SweAfConfig config,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        BuildNotifier notifier,
        ILogger<SweAfService> logger)
    {
        _http      = http;
        _config    = config;
        _dbFactory = dbFactory;
        _notifier  = notifier;
        _logger    = logger;
    }

    // ── Retry ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-triggers a failed or cancelled build with the same goal and repository URL.
    /// </summary>
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

    /// <summary>
    /// Registers (or updates) the observability webhook on the AgentField control plane.
    /// After registration, AgentField will POST execution events to <paramref name="url"/>.
    /// </summary>
    public async Task<(bool Success, string? Error)> RegisterWebhookAsync(
        string url, string? secret, CancellationToken ct = default)
    {
        object payload = string.IsNullOrWhiteSpace(secret)
            ? new { url }
            : new { url, secret };

        var resp = await _http.PostAsJsonAsync(
            "/api/v1/settings/observability-webhook", payload, ct);

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
        if (!_config.IsConfigured)
            throw new InvalidOperationException(
                "SweAf is not configured. Set SweAf:BaseUrl and SweAf:ApiKey in appsettings.json.");

        var payload = new
        {
            input = new
            {
                goal,
                repo_url = repoUrl,
                config   = new
                {
                    runtime = _config.Runtime,
                    models  = _config.Models,
                },
            },
        };

        var resp = await _http.PostAsJsonAsync(
            "/api/v1/execute/async/swe-planner.build",
            payload,
            new System.Text.Json.JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
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
        _logger.LogInformation("SWE-AF build triggered: {ExternalJobId} for {RepoUrl}", job.ExternalJobId, repoUrl);
        return job;
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    public async Task<SweAfJobEntity?> GetJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SweAfJobs.FindAsync([jobId], ct);
    }

    /// <summary>
    /// Fetches live execution detail from the AgentField control plane.
    /// Returns <see langword="null"/> when SWE-AF is not configured or the request fails.
    /// </summary>
    public async Task<BuildExecutionDetail?> FetchExecutionDetailAsync(
        string externalJobId, CancellationToken ct = default)
    {
        if (!_config.IsConfigured) return null;
        try
        {
            var resp = await _http.GetAsync($"/api/v1/executions/{externalJobId}", ct);
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

    /// <summary>
    /// Sends a cancel request to AgentField. The local job status will be updated
    /// asynchronously when the resulting <c>execution_cancelled</c> webhook event arrives.
    /// </summary>
    public async Task<(bool Success, string? Error)> CancelJobAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var db  = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.SweAfJobs.FindAsync([jobId], ct);
        if (job is null) return (false, "Job not found.");

        var resp = await _http.PostAsync(
            $"/api/v1/executions/{job.ExternalJobId}/cancel", content: null, ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cancel request for {JobId} returned {Status}",
                jobId, (int)resp.StatusCode);
            return (false, $"AgentField returned {(int)resp.StatusCode}.");
        }

        _logger.LogInformation("Cancel request sent for {ExternalJobId}", job.ExternalJobId);
        return (true, null);
    }

    /// <summary>
    /// Sends an approval decision to AgentField for a job currently in the
    /// <see cref="BuildStatus.Waiting"/> state.
    /// </summary>
    public async Task<(bool Success, string? Error)> ApproveJobAsync(
        long jobId, bool approved, CancellationToken ct = default)
    {
        await using var db  = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.SweAfJobs.FindAsync([jobId], ct);
        if (job is null) return (false, "Job not found.");

        var payload = new { execution_id = job.ExternalJobId, approved };
        var resp    = await _http.PostAsJsonAsync("/api/v1/webhooks/approval-response", payload, ct);

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
        await using var db  = await _dbFactory.CreateDbContextAsync();
        var job = await db.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == externalJobId);

        if (job is null)
        {
            // Externally triggered run we haven't seen before — upsert on creation events.
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

    /// <summary>
    /// Polls AgentField for the current status of each supplied execution ID.
    /// Used by <see cref="SweAfRecoveryService"/> on hub restart to close any
    /// state gap that accumulated while the hub was offline.
    /// </summary>
    public async Task ReconcileAsync(IEnumerable<string> externalJobIds, CancellationToken ct = default)
    {
        foreach (var id in externalJobIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await _http.GetAsync($"/api/v1/executions/{id}", ct);
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

    /// <summary>
    /// Verifies the HMAC-SHA256 signature on an incoming AgentField webhook request.
    /// Returns <see langword="true"/> if no secret is configured (verification disabled).
    /// </summary>
    public static bool VerifySignature(string? secret, byte[] body, string? signature)
    {
        if (string.IsNullOrEmpty(secret)) return true;
        if (string.IsNullOrEmpty(signature)) return false;

        // Strip optional "sha256=" prefix
        var hex = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature[7..]
            : signature;

        try
        {
            using var hmac     = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var computed       = hmac.ComputeHash(body);
            var expected       = Convert.FromHexString(hex);
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
        "queued"             => BuildStatus.Queued,
        "running"            => BuildStatus.Running,
        "waiting" or "paused"=> BuildStatus.Waiting,
        "succeeded"          => BuildStatus.Succeeded,
        "failed"             => BuildStatus.Failed,
        "cancelled"          => BuildStatus.Cancelled,
        _                    => BuildStatus.Running,
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
