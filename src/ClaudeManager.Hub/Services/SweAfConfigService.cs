using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// DB-backed singleton for the AgentField connection config.
/// Loads from the database at startup (IHostedService.StartAsync) and caches the result
/// so that IsConfigured and other sync properties are immediately readable.
/// </summary>
public class SweAfConfigService : IHostedService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly IConfiguration? _config;
    private readonly ILogger<SweAfConfigService> _logger;
    private SweAfConfigEntity _cache = new();

    public SweAfConfigService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        ILogger<SweAfConfigService> logger,
        IConfiguration? config = null)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
        _config    = config;
    }

    // ── Sync properties (from cache, updated on save) ─────────────────────────

    public bool IsConfigured =>
        _cache.IsConfigured ||
        (!string.IsNullOrWhiteSpace(_config?["SweAf:BaseUrl"]) &&
         !string.IsNullOrWhiteSpace(_config?["SweAf:ApiKey"]));

    /// <summary>DB value takes precedence; falls back to appsettings for migration compatibility.</summary>
    public string? WebhookSecret =>
        _cache.WebhookSecret ?? _config?["SweAf:WebhookSecret"];

    public string? HubPublicUrl =>
        _cache.HubPublicUrl ?? _config?["SweAf:HubPublicUrl"];

    /// <summary>
    /// Returns the current cached config synchronously.
    /// The cache is populated at startup and refreshed on every Save.
    /// </summary>
    public SweAfConfigEntity GetConfig() => _cache;

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _cache = await db.SweAfConfigs.FirstOrDefaultAsync(ct) ?? new SweAfConfigEntity();
        _logger.LogInformation(
            "SWE-AF config loaded (IsConfigured={IsConfigured})", _cache.IsConfigured);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>Returns a fresh copy from the DB (bypasses cache).</summary>
    public async Task<SweAfConfigEntity> GetConfigAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SweAfConfigs.FirstOrDefaultAsync(ct) ?? new SweAfConfigEntity();
    }

    /// <summary>Upserts the single config row and refreshes the in-memory cache.</summary>
    public async Task SaveAsync(SweAfConfigEntity config, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.SweAfConfigs.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            config.Id = 0;
            db.SweAfConfigs.Add(config);
        }
        else
        {
            existing.BaseUrl        = config.BaseUrl;
            existing.ApiKey         = config.ApiKey;
            existing.WebhookSecret  = config.WebhookSecret;
            existing.HubPublicUrl   = config.HubPublicUrl;
            existing.Runtime        = config.Runtime;
            existing.ModelDefault        = config.ModelDefault;
            existing.ModelCoder          = config.ModelCoder;
            existing.ModelQa             = config.ModelQa;
            existing.DefaultRepoUrl      = config.DefaultRepoUrl;
            existing.RepositoryApiToken  = config.RepositoryApiToken;
            existing.ProvisionHost      = config.ProvisionHost;
            existing.SshUser            = config.SshUser;
            existing.SshKeyPath         = config.SshKeyPath;
            existing.SshPassword        = config.SshPassword;
            existing.SshPort            = config.SshPort;
            existing.RequiresSudo       = config.RequiresSudo;
            existing.SudoPassword       = config.SudoPassword;
            existing.AnthropicApiKey    = config.AnthropicApiKey;
            existing.SweAfRepoPath      = config.SweAfRepoPath;
            existing.LlmDeploymentId    = config.LlmDeploymentId;
        }
        await db.SaveChangesAsync(ct);

        // Keep sync properties consistent immediately after save
        _cache = await db.SweAfConfigs.FirstOrDefaultAsync(ct) ?? config;

        _logger.LogInformation(
            "SWE-AF config saved (IsConfigured={IsConfigured})", _cache.IsConfigured);
    }
}
