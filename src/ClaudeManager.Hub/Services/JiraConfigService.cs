using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Services;

public class JiraConfigService : IHostedService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly ILogger<JiraConfigService> _logger;
    private JiraConfigEntity _cache = new();

    public JiraConfigService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        ILogger<JiraConfigService> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public bool IsConfigured => _cache.IsConfigured;

    public JiraConfigEntity GetConfig() => _cache;

    public async Task StartAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _cache = await db.JiraConfigs.FirstOrDefaultAsync(ct) ?? new JiraConfigEntity();
        _logger.LogInformation("Jira config loaded (IsConfigured={IsConfigured})", _cache.IsConfigured);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<JiraConfigEntity> GetConfigAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.JiraConfigs.FirstOrDefaultAsync(ct) ?? new JiraConfigEntity();
    }

    public async Task SaveAsync(JiraConfigEntity config, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.JiraConfigs.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            config.Id = 0;
            db.JiraConfigs.Add(config);
        }
        else
        {
            existing.BaseUrl             = config.BaseUrl;
            existing.Email               = config.Email;
            existing.ApiToken            = config.ApiToken;
            existing.DefaultProjectKey   = config.DefaultProjectKey;
            existing.DefaultJql          = config.DefaultJql;
            existing.DefaultRepoUrl      = config.DefaultRepoUrl;
            existing.OnDeckStatusName    = config.OnDeckStatusName;
            existing.ReviewStatusName    = config.ReviewStatusName;
            existing.PollingIntervalSecs = config.PollingIntervalSecs;
            existing.WebhookSecret       = config.WebhookSecret;
        }
        await db.SaveChangesAsync(ct);
        _cache = await db.JiraConfigs.FirstOrDefaultAsync(ct) ?? config;
        _logger.LogInformation("Jira config saved (IsConfigured={IsConfigured})", _cache.IsConfigured);
    }
}
