using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SweAfConfigServiceTests
{
    private SqliteConnection _conn = default!;
    private IDbContextFactory<ClaudeManagerDbContext> _dbFactory = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn      = conn;
        _dbFactory = factory;
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    private SweAfConfigService CreateService() =>
        new(_dbFactory, NullLogger<SweAfConfigService>.Instance);

    // ── StartAsync ────────────────────────────────────────────────────────────

    [Test]
    public async Task StartAsync_EmptyDb_CachesDefaultEntity()
    {
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        svc.GetConfig().Should().NotBeNull();
        svc.GetConfig().BaseUrl.Should().BeEmpty();
    }

    [Test]
    public async Task StartAsync_ExistingRow_LoadsIntoCache()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfConfigs.Add(new SweAfConfigEntity { BaseUrl = "https://af.test", ApiKey = "k" });
            await db.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        svc.GetConfig().BaseUrl.Should().Be("https://af.test");
        svc.GetConfig().ApiKey.Should().Be("k");
    }

    // ── GetConfigAsync ────────────────────────────────────────────────────────

    [Test]
    public async Task GetConfigAsync_EmptyDb_ReturnsDefaultEntity()
    {
        var svc = CreateService();
        var cfg = await svc.GetConfigAsync();
        cfg.Should().NotBeNull();
        cfg.BaseUrl.Should().BeEmpty();
    }

    [Test]
    public async Task GetConfigAsync_BypassesCache()
    {
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        // Write directly to DB after service started (bypasses cache)
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfConfigs.Add(new SweAfConfigEntity { BaseUrl = "https://direct.test" });
            await db.SaveChangesAsync();
        }

        var fresh = await svc.GetConfigAsync();
        fresh.BaseUrl.Should().Be("https://direct.test");
        // Cache still has old value
        svc.GetConfig().BaseUrl.Should().BeEmpty();
    }

    // ── SaveAsync – insert ────────────────────────────────────────────────────

    [Test]
    public async Task SaveAsync_Insert_PersistsAllCoreFields()
    {
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        var cfg = new SweAfConfigEntity
        {
            BaseUrl               = "https://af.test",
            ApiKey                = "key-1",
            WebhookSecret         = "wh-secret",
            HubPublicUrl          = "https://hub.test",
            Runtime               = "open_code",
            ModelDefault          = "foxhire-ai-1/qwen3.6-35b",
            ModelCoder            = "foxhire-ai-1/qwen3.6-35b",
            ModelQa               = "foxhire-ai-1/qwen3.6-35b",
            DefaultRepoUrl        = "https://github.com/org/repo",
            RepositoryApiToken    = "ghp_token",
            ProvisionHost         = "192.168.1.10",
            SshUser               = "ubuntu",
            SshKeyPath            = "/home/user/.ssh/id_rsa",
            SshPassword           = "pass",
            SshPort               = 2222,
            RequiresSudo          = true,
            SudoPassword          = "sudo-pass",
            AnthropicApiKey       = "sk-ant",
            SweAfRepoPath         = "/opt/swe-af",
            LlmDeploymentId       = "deploy-1",
            ComposeOverride       = "services:\n  foo:\n    image: bar",
            PortRangeStart        = 9000,
            PortRangeEnd          = 9099,
            ControlPlaneImageTag  = "v1.2.3",
            CavemanEnabled        = true,
            OpencodeJsonTemplate  = "{\"model\":\"foxhire-ai-1/qwen3.6-35b\"}",
        };

        await svc.SaveAsync(cfg);

        var saved = await svc.GetConfigAsync();
        saved.BaseUrl.Should().Be("https://af.test");
        saved.ApiKey.Should().Be("key-1");
        saved.WebhookSecret.Should().Be("wh-secret");
        saved.HubPublicUrl.Should().Be("https://hub.test");
        saved.Runtime.Should().Be("open_code");
        saved.ModelDefault.Should().Be("foxhire-ai-1/qwen3.6-35b");
        saved.ModelCoder.Should().Be("foxhire-ai-1/qwen3.6-35b");
        saved.ModelQa.Should().Be("foxhire-ai-1/qwen3.6-35b");
        saved.DefaultRepoUrl.Should().Be("https://github.com/org/repo");
        saved.RepositoryApiToken.Should().Be("ghp_token");
        saved.ProvisionHost.Should().Be("192.168.1.10");
        saved.SshUser.Should().Be("ubuntu");
        saved.SshKeyPath.Should().Be("/home/user/.ssh/id_rsa");
        saved.SshPassword.Should().Be("pass");
        saved.SshPort.Should().Be(2222);
        saved.RequiresSudo.Should().BeTrue();
        saved.SudoPassword.Should().Be("sudo-pass");
        saved.AnthropicApiKey.Should().Be("sk-ant");
        saved.SweAfRepoPath.Should().Be("/opt/swe-af");
        saved.LlmDeploymentId.Should().Be("deploy-1");
        saved.ComposeOverride.Should().Be("services:\n  foo:\n    image: bar");
        saved.PortRangeStart.Should().Be(9000);
        saved.PortRangeEnd.Should().Be(9099);
        saved.ControlPlaneImageTag.Should().Be("v1.2.3");
        saved.CavemanEnabled.Should().BeTrue();
        saved.OpencodeJsonTemplate.Should().Be("{\"model\":\"foxhire-ai-1/qwen3.6-35b\"}");
    }

    // ── SaveAsync – update (upsert) ───────────────────────────────────────────

    [Test]
    public async Task SaveAsync_Update_OverwritesExistingRow()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfConfigs.Add(new SweAfConfigEntity { BaseUrl = "https://old.test", ApiKey = "old-key" });
            await db.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        await svc.SaveAsync(new SweAfConfigEntity { BaseUrl = "https://new.test", ApiKey = "new-key" });

        var updated = await svc.GetConfigAsync();
        updated.BaseUrl.Should().Be("https://new.test");
        updated.ApiKey.Should().Be("new-key");

        await using var db2 = _dbFactory.CreateDbContext();
        db2.SweAfConfigs.Should().HaveCount(1);
    }

    [Test]
    public async Task SaveAsync_Update_PersistsOpencodeJsonTemplate()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfConfigs.Add(new SweAfConfigEntity { BaseUrl = "https://af.test", ApiKey = "k" });
            await db.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        var template = "{\"model\":\"foxhire-ai-1/qwen3.6-35b\",\"provider\":{\"foxhire-ai-1\":{\"options\":{\"baseURL\":\"{ProxyUrl}\"}}}}";
        await svc.SaveAsync(new SweAfConfigEntity
        {
            BaseUrl              = "https://af.test",
            ApiKey               = "k",
            OpencodeJsonTemplate = template,
        });

        var saved = await svc.GetConfigAsync();
        saved.OpencodeJsonTemplate.Should().Be(template);
    }

    [Test]
    public async Task SaveAsync_Update_NullOpencodeTemplate_ClearsExistingValue()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.SweAfConfigs.Add(new SweAfConfigEntity
            {
                BaseUrl              = "https://af.test",
                ApiKey               = "k",
                OpencodeJsonTemplate = "{\"old\":true}",
            });
            await db.SaveChangesAsync();
        }

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        await svc.SaveAsync(new SweAfConfigEntity { BaseUrl = "https://af.test", ApiKey = "k", OpencodeJsonTemplate = null });

        var saved = await svc.GetConfigAsync();
        saved.OpencodeJsonTemplate.Should().BeNull();
    }

    // ── SaveAsync – cache invalidation ────────────────────────────────────────

    [Test]
    public async Task SaveAsync_RefreshesInMemoryCache()
    {
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        svc.GetConfig().BaseUrl.Should().BeEmpty();

        await svc.SaveAsync(new SweAfConfigEntity { BaseUrl = "https://cached.test", ApiKey = "k" });

        svc.GetConfig().BaseUrl.Should().Be("https://cached.test");
    }

    [Test]
    public async Task SaveAsync_CacheReflectsOpencodeTemplate()
    {
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        await svc.SaveAsync(new SweAfConfigEntity
        {
            BaseUrl              = "https://af.test",
            ApiKey               = "k",
            OpencodeJsonTemplate = "{\"model\":\"test\"}",
        });

        svc.GetConfig().OpencodeJsonTemplate.Should().Be("{\"model\":\"test\"}");
    }

    // ── IsConfigured ──────────────────────────────────────────────────────────

    [Test]
    public async Task IsConfigured_BothSet_ReturnsTrue()
    {
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);
        await svc.SaveAsync(new SweAfConfigEntity { BaseUrl = "https://af.test", ApiKey = "k" });

        svc.IsConfigured.Should().BeTrue();
    }

    [Test]
    public async Task IsConfigured_EmptyDb_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        svc.IsConfigured.Should().BeFalse();
    }

    [Test]
    public async Task IsConfigured_MissingApiKey_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);
        await svc.SaveAsync(new SweAfConfigEntity { BaseUrl = "https://af.test", ApiKey = null });

        svc.IsConfigured.Should().BeFalse();
    }
}
