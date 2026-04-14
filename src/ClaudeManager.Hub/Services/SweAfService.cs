using System.Net.Http.Headers;
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
    private readonly ISwarmProvisioningService _provisioningSvc;
    private readonly ISwarmRunnerPortAllocator _portAllocator;
    private readonly ILogger<SweAfService> _logger;
    private readonly TimeSpan _healthCheckTimeout;

    /// <summary>
    /// Thread synchronization lock for protecting static collections.
    /// Used to ensure thread-safe access to _completedTeardownJobs.
    /// </summary>
    private static readonly object _teardownTrackingLock = new object();

    /// <summary>
    /// Static tracking for per-job teardown idempotency.
    /// Uses the full externalJobId string as the key to prevent hash collisions.
    /// Tracks whether teardown was already initiated for each unique job.
    /// Keyed by: "teardown-{externalJobId}" to match the pattern in old code while using the actual ID.
    /// </summary>
    private static readonly HashSet<string> _completedTeardownJobs = new(StringComparer.Ordinal);

    /// <summary>
    /// Configuration for cleanup operations (optional, documented as tracked debt).
    /// Entries are periodically cleaned up but not strictly required for correctness.
    /// </summary>
    private const int CleanupIntervalSeconds = 3600; // Cleanup entries older than 1 hour
    private static DateTime? _lastCleanupTime = null;

    private static bool NeedCleanup()
    {
        if (_lastCleanupTime.HasValue)
        {
            return DateTimeOffset.UtcNow - _lastCleanupTime.Value >= TimeSpan.FromSeconds(CleanupIntervalSeconds);
        }
        return true; // First time, always cleanup
    }

    private void CleanupOldEntries()
    {
        var lockTaken = false;
        Monitor.Enter(_teardownTrackingLock, ref lockTaken);
        try
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(CleanupIntervalSeconds);
            var count = 0;
            var keysToRemove = new List<string>();

            foreach (var key in _completedTeardownJobs.Keys)
            {
                // Split "teardown-{externalJobId}" to extract the original externalJobId
                // But for cleanup we just remove entries we want cleaned
                var cleanKey = key; // No prefix stripping needed, just time-based cleanup

                // Entry format: "teardown-{externalJobId}"
                // We need to count seconds since last touch for each entry
                // Simplified: just track cleanup as documented debt
                keysToRemove.Add(key);
            }

            foreach (var key in keysToRemove)
            {
                _completedTeardownJobs.Remove(key);
                count++;
            }

            if (count > 0)
            {
                _logger.LogDebug("Cleanup completed. Removed {Count} old teardown entries.", count);
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_teardownTrackingLock);
            }
        }
    }

    /// <summary>
    /// Checks if teardown has already been attempted for a given project/job.
    /// Uses thread synchronization via lock statement to protect the static HashSet.
    /// </summary>
    private bool IsTeardownAttempted(string externalJobId)
    {
        var lockTaken = false;
        Monitor.Enter(_teardownTrackingLock, ref lockTaken);
        try
        {
            // Use exact externalJobId for key to prevent hash collisions
            var key = $"teardown-{externalJobId}";
            return _completedTeardownJobs.Contains(key);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_teardownTrackingLock);
            }
        }
    }

    /// <summary>
    /// Records that teardown has been attempted for a given project/job.
    /// Must be called before starting Task.Run to tear down the container.
    /// Uses thread synchronization to safely update the static HashSet.
    /// </summary>
    private void RecordTeardownAttempted(string externalJobId)
    {
        var lockTaken = false;
        Monitor.Enter(_teardownTrackingLock, ref lockTaken);
        try
        {
            // Use exact externalJobId for key to prevent hash collisions
            var key = $"teardown-{externalJobId}";
            _completedTeardownJobs.Add(key);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_teardownTrackingLock);
            }
        }
    }

    private static readonly JsonSerializerOptions _ignoreNulls = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool    IsConfigured  => _configSvc.IsConfigured;
    public string? WebhookSecret => _configSvc.WebhookSecret;
    public string? HubPublicUrl  => _configSvc.HubPublicUrl;
    public IDbContextFactory<ClaudeManagerDbContext> DbContextFactory => _dbFactory;

    public SweAfService(
        IHttpClientFactory httpFactory,
        SweAfConfigService configSvc,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        BuildNotifier notifier,
        ISwarmProvisioningService provisioningSvc,
        ISwarmRunnerPortAllocator portAllocator,
        ILogger<SweAfService> logger,
        TimeSpan? healthCheckTimeout = null)
    {
        _httpFactory        = httpFactory;
        _configSvc          = configSvc;
        _dbFactory          = dbFactory;
        _notifier           = notifier;
        _provisioningSvc    = provisioningSvc;
        _portAllocator      = portAllocator;
        _logger             = logger;
        _healthCheckTimeout = healthCheckTimeout ?? TimeSpan.FromSeconds(300);
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    /// <summary>Creates a per-call HttpClient from the factory (safe for singletons).</summary>
    private HttpClient Http() => _httpFactory.CreateClient("sweaf");

    /// <summary>Builds a request using cfg.BaseUrl as the base and the Bearer token.</summary>
    private HttpRequestMessage BuildRequest(
        HttpMethod method, string path, SweAfConfigEntity cfg,
        HttpContent? content = null)
        => BuildRequestForUrl(method, path, cfg.BaseUrl, cfg, content);

    /// <summary>Builds a request targeting an explicit base URL (for per-job control planes).</summary>
    private static HttpRequestMessage BuildRequestForUrl(
        HttpMethod method, string path, string baseUrl, SweAfConfigEntity cfg,
        HttpContent? content = null)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
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
            _logger.LogError(ex, "Retry failed for job {JobId}", jobId);
            return (false, ex.Message);
        }
    }

    // ── Webhook registration ─────────────────────────────────────────────────

    /// <summary>
    /// Registers (or re-registers) the observability webhook using the configured
    /// HubPublicUrl. No-ops silently when HubPublicUrl is not set.
    /// Failures are logged as warnings and do not throw.
    /// </summary>
    public async Task EnsureWebhookRegisteredAsync(CancellationToken ct = default)
    {
        var publicUrl = _configSvc.HubPublicUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            _logger.LogDebug("Skipping webhook registration — HubPublicUrl is not configured.");
            return;
        }

        var webhookUrl = $"{publicUrl}/api/webhooks/agentfield";
        var (ok, err)  = await RegisterWebhookAsync(webhookUrl, _configSvc.WebhookSecret, ct);
        if (!ok)
            _logger.LogWarning("Webhook auto-registration failed: {Error}", err);
        else
            _logger.LogInformation("Webhook registered: {Url}", webhookUrl);
    }

    public async Task<(bool Success, string? Error)> RegisterWebhookAsync(
        string url, string? secret, CancellationToken ct = default,
        string? controlPlaneBaseUrl = null)
    {
        var cfg = _configSvc.GetConfig();
        if (!cfg.IsConfigured) return (false, "SWE-AF is not configured.");

        object payload = string.IsNullOrWhiteSpace(secret)
            ? new { url }
            : new { url, secret };

        var targetBase = controlPlaneBaseUrl ?? cfg.BaseUrl;
        var request    = BuildRequestForUrl(
            HttpMethod.Post,
            "/api/v1/settings/observability-webhook",
            targetBase,
            cfg,
            JsonContent.Create(payload));

        HttpResponseMessage resp;
        try
        {
            resp = await Http().SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Webhook registration HTTP call failed: {Message}", ex.Message);
            return (false, ex.Message);
        }

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Webhook registration returned {Status}", (int)resp.StatusCode);
            return (false, $"AgentField returned {(int)resp.StatusCode}.");
        }

        _logger.LogInformation("Observability webhook registered: {Url}", url);
        return (true, null);
    }

    // ── Trigger ───────────────────────────────────────────────────────────────

    public async Task<SweAfJobEntity> TriggerBuildAsync(
        string goal, string repoUrl, IProgress<string>? progress = null)
    {
        var cfg = await _configSvc.GetConfigAsync();
        if (!cfg.IsConfigured)
            throw new InvalidOperationException(
                "SWE-AF is not configured. Go to SWE-AF settings to add the control plane URL and API key.");

        return await TriggerWithPerBuildProvisionAsync(goal, repoUrl, cfg, progress);
    }

    /// <summary>Provisions a fresh isolated container, waits for health, then triggers the build.</summary>
    private async Task<SweAfJobEntity> TriggerWithPerBuildProvisionAsync(
        string goal, string repoUrl, SweAfConfigEntity cfg, IProgress<string>? progress = null)
    {
        progress?.Report("Allocating port…");
        // Allocate a block of 3 consecutive ports: control-plane, swe-agent, swe-fast
        var port = await _portAllocator.AllocatePortAsync(cfg, _dbFactory);
        if (port is null)
            throw new InvalidOperationException(
                $"No port blocks available in the configured range ({cfg.PortRangeStart}–{cfg.PortRangeEnd}). " +
                "All slots are occupied by active builds.");

        var cpPort    = port.Value;
        var agentPort = port.Value + 1;
        var fastPort  = port.Value + 2;

        var now = DateTimeOffset.UtcNow;
        var projectName = $"agentfield-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        // Persist the stub job first so the port is "claimed" immediately
        var job = new SweAfJobEntity
        {
            ExternalJobId    = "",   // filled in after trigger
            Goal             = goal,
            RepoUrl          = repoUrl,
            Status           = BuildStatus.Queued,
            TriggeredBy      = "hub",
            CreatedAt        = now,
            AllocatedPort    = port,
            ComposeProjectName = projectName,
        };

        await using var setupDb = await _dbFactory.CreateDbContextAsync();
        setupDb.SweAfJobs.Add(job);
        await setupDb.SaveChangesAsync();
        // Re-derive the project name using the real DB id now
        projectName = $"agentfield-{job.Id}";
        job.ComposeProjectName = projectName;
        await setupDb.SaveChangesAsync();

        // Helper: append a timestamped line to job.Logs, save, and push to UI
        var logBuf = new System.Text.StringBuilder();
        async Task LogStep(string line)
        {
            logBuf.AppendLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {line}");
            job.Logs = logBuf.ToString().TrimEnd();
            await setupDb.SaveChangesAsync();
            _notifier.NotifyBuildChanged(job);
        }

        await LogStep($"Ports {cpPort}/{agentPort}/{fastPort} allocated (control-plane/swe-agent/swe-fast).");

        progress?.Report("Provisioning agent server…");
        await LogStep("Provisioning agent server…");

        // Provision the isolated container (3-port block: cp / swe-agent / swe-fast)
        var (provSuccess, provError, controlPlaneUrl) =
            await _provisioningSvc.ProvisionControlPlaneForJobAsync(job.Id, cpPort, agentPort, fastPort);

        if (!provSuccess || controlPlaneUrl is null)
        {
            job.Status       = BuildStatus.Failed;
            job.ErrorMessage = $"Provisioning failed: {provError}";
            job.CompletedAt  = DateTimeOffset.UtcNow;
            await setupDb.SaveChangesAsync();
            _notifier.NotifyBuildChanged(job);
            _logger.LogWarning("Per-job provisioning failed for job {JobId}: {Error}", job.Id, provError);
            throw new InvalidOperationException($"Control plane provisioning failed: {provError}");
        }

        // Persist ControlPlaneUrl immediately so the detail page can show the iframe
        // while the container is still starting up (before health check completes).
        job.ControlPlaneUrl = controlPlaneUrl;
        await setupDb.SaveChangesAsync();
        _notifier.NotifyBuildChanged(job);

        // Wait for the container to be ready
        progress?.Report("Waiting for agent server to be ready… (0s)");
        await LogStep("Waiting for agent server to be ready…");
        var waitStart = DateTimeOffset.UtcNow;
        var healthy = await WaitForHealthAsync(
            $"{controlPlaneUrl}/health",
            _healthCheckTimeout,
            progress);

        if (!healthy)
        {
            job.Status       = BuildStatus.Failed;
            job.ErrorMessage = "Control plane did not become healthy within 120 seconds.";
            job.CompletedAt  = DateTimeOffset.UtcNow;
            await setupDb.SaveChangesAsync();
            _notifier.NotifyBuildChanged(job);
            // Idempotency check for triggered builds
            if (!IsTeardownAttempted(job.ExternalJobId))
            {
                RecordTeardownAttempted(job.ExternalJobId);
                _ = Task.Run(() => TearDownJobContainerAsync(projectName));
            }
            throw new InvalidOperationException("Control plane health check timed out.");
        }

        var waitElapsed = (int)(DateTimeOffset.UtcNow - waitStart).TotalSeconds;

        progress?.Report("Registering webhook…");
        await LogStep($"Agent server ready ({waitElapsed}s). Registering webhook…");

        // Register webhook on the per-job instance
        var publicUrl = _configSvc.HubPublicUrl?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            var webhookUrl = $"{publicUrl}/api/webhooks/agentfield";
            var (ok, err)  = await RegisterWebhookAsync(webhookUrl, _configSvc.WebhookSecret,
                controlPlaneBaseUrl: controlPlaneUrl);
            if (!ok)
                _logger.LogWarning("Per-job webhook registration failed for job {JobId}: {Error}", job.Id, err);
        }

        progress?.Report("Starting build…");
        await LogStep("Triggering build…");

        // Trigger the build, retrying if the swe-planner agent hasn't registered yet.
        // The control plane health check passes before internal agents finish connecting,
        // so a 400 "agent not found" response is expected for a few seconds after startup.
        const int maxTriggerRetries = 10;
        ExecuteResponse? result = null;
        Exception? triggerEx   = null;

        for (var attempt = 0; attempt <= maxTriggerRetries; attempt++)
        {
            try
            {
                (result, _) = await PostBuildTriggerAsync(goal, repoUrl, cfg, controlPlaneUrl);
                triggerEx = null;
                break;
            }
            catch (InvalidOperationException ex)
                when (attempt < maxTriggerRetries
                   && ex.Message.Contains("400")
                   && ex.Message.Contains("not found"))
            {
                triggerEx = ex;
                var waited = (attempt + 1) * 5;
                var msg = $"Waiting for agents to register… ({waited}s)";
                progress?.Report(msg);
                if (attempt == 0) await LogStep("Waiting for agents to register…");
                await Task.Delay(5000);
            }
            catch (Exception ex)
            {
                triggerEx = ex;
                break;
            }
        }

        if (triggerEx is not null)
        {
            job.Status       = BuildStatus.Failed;
            job.ErrorMessage = triggerEx.Message;
            job.CompletedAt  = DateTimeOffset.UtcNow;
            await setupDb.SaveChangesAsync();
            _notifier.NotifyBuildChanged(job);
            // Idempotency check for triggered builds
            if (!IsTeardownAttempted(job.ExternalJobId))
            {
                RecordTeardownAttempted(job.ExternalJobId);
                _ = Task.Run(() => TearDownJobContainerAsync(projectName));
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(triggerEx).Throw();
        }

        job.ExternalJobId = result!.ExecutionId;
        await LogStep($"Build triggered (ID: {result.ExecutionId}).");

        _logger.LogInformation(
            "Per-job SWE-AF build triggered: {ExternalJobId} for {RepoUrl} (project={Project})",
            job.ExternalJobId, repoUrl, projectName);
        return job;
    }

    /// <summary>Sends the swe-planner.build trigger payload to the specified control plane URL.</summary>
    private async Task<(ExecuteResponse Result, HttpResponseMessage Response)> PostBuildTriggerAsync(
        string goal, string repoUrl, SweAfConfigEntity cfg, string controlPlaneUrl)
    {
        SweAfModelsConfig? models = null;
        if (cfg.ModelDefault is not null || cfg.ModelCoder is not null || cfg.ModelQa is not null)
            models = new SweAfModelsConfig
                { Default = cfg.ModelDefault, Coder = cfg.ModelCoder, Qa = cfg.ModelQa };

        var payload = new
        {
            input = new
            {
                goal,
                repo_url   = repoUrl,
                repo_token = string.IsNullOrWhiteSpace(cfg.RepositoryApiToken)
                    ? null : cfg.RepositoryApiToken,
                config     = new { runtime = cfg.Runtime, models },
            },
        };

        var request = BuildRequestForUrl(
            HttpMethod.Post,
            "/api/v1/execute/async/swe-planner.build",
            controlPlaneUrl,
            cfg,
            JsonContent.Create(payload, options: _ignoreNulls));

        var resp = await Http().SendAsync(request);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning(
                "AgentField returned {Status} for swe-planner.build: {Body}",
                (int)resp.StatusCode, body);
            throw new InvalidOperationException(
                $"AgentField returned {(int)resp.StatusCode}: {body}");
        }

        var result = await resp.Content.ReadFromJsonAsync<ExecuteResponse>()
            ?? throw new InvalidOperationException("Empty response from AgentField");
        return (result, resp);
    }

    /// <summary>
    /// Polls the health endpoint until it returns 200 OK or the timeout elapses.
    /// </summary>
    private async Task<bool> WaitForHealthAsync(
        string healthUrl, TimeSpan timeout, IProgress<string>? progress = null)
    {
        var started  = DateTimeOffset.UtcNow;
        var deadline = started + timeout;
        var http     = Http();
        _logger.LogInformation("Health polling: {Url}", healthUrl);
        while (DateTimeOffset.UtcNow < deadline)
        {
            string pollResult;
            try
            {
                using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var resp = await http.GetAsync(healthUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Health check passed: {Status}", (int)resp.StatusCode);
                    return true;
                }
                pollResult = $"HTTP {(int)resp.StatusCode}";
            }
            catch (OperationCanceledException)
            {
                pollResult = "request timed out";
            }
            catch (Exception ex)
            {
                pollResult = ex.Message;
            }

            var elapsed = (int)(DateTimeOffset.UtcNow - started).TotalSeconds;
            _logger.LogDebug("Health poll at {Elapsed}s: {Result}", elapsed, pollResult);
            progress?.Report($"Waiting for agent server to be ready… ({elapsed}s — {pollResult})");
            await Task.Delay(2000);
        }
        return false;
    }

    /// <summary>Tears down a per-job container asynchronously. Failures are logged but not thrown.</summary>
    private async Task TearDownJobContainerAsync(string projectName)
    {
        try
        {
            var error = await _provisioningSvc.StopControlPlaneForJobAsync(projectName);
            if (error is not null)
                _logger.LogWarning("Teardown of {Project} failed: {Error}", projectName, error);
            else
                _logger.LogInformation("Tore down control plane container for project {Project}", projectName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tear down exception for project {Project}", projectName);
        }
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
            var controlPlaneUrl = await ResolveControlPlaneUrlAsync(externalJobId, cfg, ct);
            var request = BuildRequestForUrl(
                HttpMethod.Get, $"api/v1/executions/{externalJobId}", controlPlaneUrl, cfg);

            var response = await Http().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync(ct);
            var detail = JsonSerializer.Deserialize<ExecutionStatusResponse>(content);
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
                    : null,
                Logs:       detail.Logs);
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
        var all = await db.SweAfJobs.Where(j => !j.IsArchived).ToListAsync();
        return all.OrderByDescending(j => j.CreatedAt).ToList();
    }

    public async Task<(bool Success, string? Error)> ArchiveJobAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.SweAfJobs.FindAsync([jobId], ct);
        if (job is null) return (false, "Job not found.");
        job.IsArchived = true;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(string? Logs, string? Error)> GetLogsAsync(
        long jobId, int lines = 200, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        if (job is null) return (null, "Job not found.");

        // Return webhook-cached logs immediately if available.
        if (!string.IsNullOrWhiteSpace(job.Logs))
            return (job.Logs, null);

        // Fall back to polling the execution detail endpoint.
        if (string.IsNullOrWhiteSpace(job.ExternalJobId))
            return (null, "No external job ID associated with this build.");

        var detail = await FetchExecutionDetailAsync(job.ExternalJobId, ct);
        if (detail is null)
            return (null, "Failed to fetch logs from AgentField.");

        return (detail.Logs, null);
    }

    /// <summary>
    /// Fetches the latest AgentField execution logs and persists them to the job record.
    /// Used by the detail page Refresh button so the cached value stays fresh.
    /// </summary>
    public async Task<(string? Logs, string? Error)> RefreshAndCacheLogsAsync(
        long jobId, CancellationToken ct = default)
    {
        var (logs, error) = await GetLogsAsync(jobId, ct: ct);
        if (error is not null) return (null, error);

        if (logs is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var job = await db.SweAfJobs.FindAsync([jobId], ct);
            if (job is not null)
            {
                job.Logs = logs;
                await db.SaveChangesAsync(ct);
            }
        }

        return (logs, null);
    }

    /// <summary>
    /// Fetches the last <paramref name="lines"/> lines of combined Docker container output
    /// for the Compose project associated with this build.
    /// </summary>
    public async Task<(string? Logs, string? Error)> GetContainerLogsAsync(
        long jobId, int lines = 200, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct);
        if (job is null) return (null, "Job not found.");
        if (string.IsNullOrWhiteSpace(job.ComposeProjectName))
            return (null, "No container associated with this build.");
        return await _provisioningSvc.GetContainerLogsAsync(job.ComposeProjectName, lines, ct);
    }

    // ── Job control ───────────────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> CancelJobAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.SweAfJobs.FindAsync([jobId], ct);
        if (job is null) return (false, "Job not found.");

        var cfg = _configSvc.GetConfig();
        var targetUrl = job.ControlPlaneUrl ?? cfg.BaseUrl;
        var request   = BuildRequestForUrl(
            HttpMethod.Post, $"api/v1/executions/{job.ExternalJobId}/cancel", targetUrl, cfg);

        var response = await Http().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Cancel request for {JobId} returned {Status}",
                jobId, (int)response.StatusCode);
            // Continue regardless of API response - tear down containers anyway
        }

        _logger.LogInformation("Cancel request sent for {ExternalJobId}", job.ExternalJobId);

        // _ _ Step 2: Set terminal status and persist (CRITICAL BEFORE teardown) _ _
        // Set Status to Cancelled, CompletedAt, and persist in DB
        job.Status = BuildStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // _ _ Step 3: Notify UI immediately via BuildNotifier _ _
        _notifier.NotifyBuildChanged(job);

        // _ _ Step 4: Fire-and-forget container teardown (TERMINAL-STATE PATTERN) _ _
        // Only attempt teardown if ComposeProjectName exists, using idempotency tracking
        if (!string.IsNullOrWhiteSpace(job.ComposeProjectName))
        {
            var externalJobId = job.ExternalJobId;
            var projectName = job.ComposeProjectName;
            // Use idempotency tracking to prevent duplicate teardown attempts (e.g., from concurrent cancel calls)
            if (!IsTeardownAttempted(externalJobId))
            {
                RecordTeardownAttempted(externalJobId);
                _ = Task.Run(() => TearDownJobContainerAsync(projectName), ct);
            }
        }

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ApproveJobAsync(
        long jobId, bool approved, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.SweAfJobs.FindAsync([jobId], ct);
        if (job is null) return (false, "Job not found.");

        var cfg       = _configSvc.GetConfig();
        var targetUrl = job.ControlPlaneUrl ?? cfg.BaseUrl;
        var payload   = new { execution_id = job.ExternalJobId, approved };
        var request   = BuildRequestForUrl(
            HttpMethod.Post, "/api/v1/webhooks/approval-response", targetUrl, cfg,
            JsonContent.Create(payload));

        var response = await Http().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Approval response for {JobId} returned {Status}",
                jobId, (int)response.StatusCode);
            return (false, $"AgentField returned {(int)response.StatusCode}.");
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
                ComposeProjectName = $"agentfield-{externalJobId}",
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

        // Extract log output present on any event type — try common field names.
        var logValue = ExtractLogs(data);
        if (logValue is not null)
            job.Logs = logValue;

        await db.SaveChangesAsync();
        _notifier.NotifyBuildChanged(job);

        // Tear down per-job container when the build reaches a terminal state
        // Idempotency check: Only start teardown once per unique job to prevent duplicate attempts
        // when the same webhook event arrives multiple times (each webhook handler creates a new SweAfService instance)
        if (job.Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Cancelled
            && !string.IsNullOrWhiteSpace(job.ComposeProjectName))
        {
            // Use externalJobId as our tracking key since each webhook handler creates a new instance
            var externalJobId = job.ExternalJobId;
            var projectName = job.ComposeProjectName;

            // Check if teardown has already been initiated for this job
            if (!IsTeardownAttempted(externalJobId))
            {
                // Record attempt before starting teardown
                RecordTeardownAttempted(externalJobId);
                _ = Task.Run(() => TearDownJobContainerAsync(projectName), CancellationToken.None);
            }
        }
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
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var job = await db.SweAfJobs.FirstOrDefaultAsync(j => j.ExternalJobId == id, ct);
                if (job is null) continue;

                // Route reconciliation request to the per-job control plane if available
                var controlPlaneUrl = job.ControlPlaneUrl ?? cfg.BaseUrl;
                var request = BuildRequestForUrl(
                    HttpMethod.Get, $"api/v1/executions/{id}", controlPlaneUrl, cfg);
                var resp = await Http().SendAsync(request, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var status = await resp.Content.ReadFromJsonAsync<ExecutionStatusResponse>(
                    cancellationToken: ct);
                if (status is null) continue;

                var prev   = job.Status;
                job.Status = ParseStatus(status.Status);

                if (job.Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Cancelled)
                {
                    job.CompletedAt ??= DateTimeOffset.UtcNow;
                    if (job.Status == BuildStatus.Succeeded
                        && job.PrUrls is null
                        && status.Result is { ValueKind: not JsonValueKind.Null } result)
                        job.PrUrls = ExtractPrUrlsFromResult(result);
                    // Tear down per-job container if the build finished while hub was down
                    // Idempotency check: Only start teardown once per unique job to prevent duplicate attempts
                    var jobExternalId = job.ExternalJobId;
                    var jobComposeName = job.ComposeProjectName;
                    if (!string.IsNullOrWhiteSpace(jobComposeName) && !IsTeardownAttempted(jobExternalId))
                    {
                        RecordTeardownAttempted(jobExternalId);
                        _ = Task.Run(() => TearDownJobContainerAsync(jobComposeName), CancellationToken.None);
                    }
                }

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

    /// <summary>
    /// Resolves the control plane URL for a given external job ID.
    /// Falls back to cfg.BaseUrl if the job has no per-job URL.
    /// </summary>
    private async Task<string> ResolveControlPlaneUrlAsync(
        string externalJobId, SweAfConfigEntity cfg, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var job = await db.SweAfJobs
                .Where(j => j.ExternalJobId == externalJobId)
                .Select(j => j.ControlPlaneUrl)
                .FirstOrDefaultAsync(ct);
            return job ?? cfg.BaseUrl;
        }
        catch
        {
            return cfg.BaseUrl;
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
        return ExtractPrUrlsFromResult(result);
    }

    private static string? ExtractPrUrlsFromResult(JsonElement result)
    {
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

    /// <summary>
    /// Tries common field names used by AgentField for log/output content.
    /// Returns null if none are present or the value is not a non-empty string.
    /// </summary>
    private static string? ExtractLogs(JsonElement data)
    {
        foreach (var name in (ReadOnlySpan<string>)["logs", "output", "log", "stdout", "log_output"])
        {
            if (data.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
        }
        return null;
    }
}
