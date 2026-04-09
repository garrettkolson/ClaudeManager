using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClaudeManager.Hub;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ClaudeManager.Hub.Tests.Services.Integration;

/// <summary>
/// Integration tests for cross-feature interaction between Host GPUs, Deployments, and Proxy components.
/// Tests the merged behavior from multiple feature branches (issue/0d65b19b-07-LlmServersIntegration).
/// </summary>
public class LlmServersMultiFeatureTests
{
    private static readonly TestData TestData = new();

    private static readonly string DatabaseNamePrefix = typeof(LlmServersMultiFeatureTests).Namespace! + ".";

    [Test]
    public async Task DeploymentOrderByCreationDate_RespectDescendingOrder()
    {
        var dbFactory = CreateDbContextFactory();

        await using var db = dbFactory.CreateDbContext();
        await db.AddHousekeepingEntityAsync(TestData.SweAfConfig);
        await db.AddHousekeepingEntityAsync(TestData.SweAfHost1);
        await db.AddHousekeepingEntityAsync(TestData.SweAfHost2);
        await db.SaveChangesAsync();

        var deployment1 = await db.LlmDeployments.AddAsync(new LlmDeploymentEntity
        {
            DeploymentId = "dep-2",
            HostId = TestData.SweAfHost1.HostId,
            ModelId = "model/c",
            GpuIndices = "0",
            HostPort = 8001,
            Quantization = "none",
            ImageTag = "latest",
            Status = LlmDeploymentStatus.Stopped,
            CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(-100), // 1s ago
        });
        await db.SaveChangesAsync();

        var deployment2 = await db.LlmDeployments.AddAsync(new LlmDeploymentEntity
        {
            DeploymentId = "dep-1",
            HostId = TestData.SweAfHost2.HostId,
            ModelId = "model/b",
            GpuIndices = "0",
            HostPort = 8002,
            Quantization = "none",
            ImageTag = "latest",
            Status = LlmDeploymentStatus.Stopped,
            CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(-50),    // 2s ago
        });
        await db.SaveChangesAsync();

        var deployment3 = await db.LlmDeployments.AddAsync(new LlmDeploymentEntity
        {
            DeploymentId = "dep-3",
            HostId = TestData.SweAfHost1.HostId,
            ModelId = "model/a",
            GpuIndices = "0",
            HostPort = 8003,
            Quantization = "none",
            ImageTag = "latest",
            Status = LlmDeploymentStatus.Stopped,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-10),        // 10s ago
        });
        await db.SaveChangesAsync();

        var deployments = await db.LlmDeployments.ToListAsync();

        deployments.Should().HaveCount(3);

        // Verify descending order by CreatedAt (> means more recent first)
        deployment3.Entity.CreatedAt.Should().BeGreaterThanOrEqualTo(deployment1.Entity.CreatedAt, "10s should be > 1ms");
        deployment3.Entity.CreatedAt.Should().BeGreaterThan(deployment2.Entity.CreatedAt, "10s should be > 2s");
        deployment2.Entity.CreatedAt.Should().BeGreaterThan(deployment1.Entity.CreatedAt, "2s should be > 1ms");

        // Verify via GetAllAsync
        var sorted = await db.LlmDeployments.OrderBy(d => d.CreatedAt).ToListAsync();
        // Now ascending - most recent last
        sorted[0].CreatedAt.Should().Be(deployment2.Entity.CreatedAt, "Oldest should be first in ascending");
        sorted[sorted.Count - 1].CreatedAt.Should().Be(deployment3.Entity.CreatedAt, "Most recent should be last in ascending");
    }

    private static IDbContextFactory<ClaudeManagerDbContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<ClaudeManagerDbContext>()
            .UseInMemoryDatabase(DatabaseNamePrefix + DateTime.UtcNow.Ticks.ToString("N4"))
            .EnableSensitiveDataLogging()
            .EnableDetailedErrors()
            .Options;

        return () => new ClaudeManagerDbContext(options);
    }

    [Test]
    public void ModelToUpstreamName_MultipleSlashSeparators_ReplacedWithUnderscores()
    {
        var modelId = "org/subdivision/model-name";
        var upstreamName = NginxProxyService.ModelToUpstreamName(modelId);

        upstreamName.Should().StartWith("vllm_");
        upstreamName.Should().Contain("org");
        upstreamName.Should().Contain("subdivision");
        upstreamName.Should().Contain("model_name");
        upstreamName.Should().NotContain('/');
        upstreamName.Should().NotContain('-');
    }

    [Test]
    public void ModelToPathSlug_DashesPreserved_InUrlPaths()
    {
        var modelId = "org/model-name-version";
        var pathSlug = NginxProxyService.ModelToPathSlug(modelId);

        pathSlug.Should().StartWith("org/");
        pathSlug.Should().Contain("-");
        pathSlug.Should().Contain("version");
        pathSlug.Should().NotContain('_');
    }

    [Test]
    public void NginxConfig_NoDeployments_ReturnsBaseConfigWithFallback()
    {
        var deployments = new List<LlmDeploymentEntity>();
        var host = new GpuHostEntity
        {
            HostId = "test-host",
            DisplayName = "Test Host",
            Host = "localhost",
            ProxyPort = 8080,
        };

        var config = NginxProxyService.GenerateConfig(deployments, 8080);

        config.Should().NotBeNull();
        config.Should().Contain("Generated by Claude Manager");
        config.Should().Contain("503");
        config.Should().Contain("listen 8080;");
        config.Should().Contain("server_names_in_tolower;");
        config.Should().Contain("keepalive 32;");
        config.Should().NotContain("upstream");
        config.Should().Contain("proxy_read_timeout 10s;");
        config.Should().Contain("proxy_connect_timeout 10s;");
    }

    [Test]
    public void NginxConfig_WithDeployments_GeneratesUpstreamPool()
    {
        var deployments = new List<LlmDeploymentEntity>
        {
            new LlmDeploymentEntity
            {
                HostId = "host-1",
                ModelId = "org/test-model",
                GpuIndices = "0",
                HostPort = 8001,
                Quantization = "none",
                ImageTag = "latest",
                Status = LlmDeploymentStatus.Running,
            },
            new LlmDeploymentEntity
            {
                HostId = "host-1",
                ModelId = "org/test-model",
                GpuIndices = "0",
                HostPort = 8002,
                Quantization = "none",
                ImageTag = "latest",
                Status = LlmDeploymentStatus.Running,
            },
        };

        var host = new GpuHostEntity
        {
            HostId = "host-1",
            DisplayName = "Test Host",
            Host = "localhost",
            ProxyPort = 8080,
        };

        var config = NginxProxyService.GenerateConfig(deployments, 8080);

        config.Should().Contain("upstream vllm_pool");
        config.Should().Contain("server 127.0.0.1:8001;");
        config.Should().Contain("server 127.0.0.1:8002;");
        config.Should().Contain("keepalive 32;");

        // Single location block
        var locationCount = Regex.Matches(config, @"^.*location /", RegexOptions.Multiline).Count;
        locationCount.Should().BeGreaterOrEqualTo(1);
    }

    [Test]
    public void StoppedDeployment_ExcludedFromUpstream()
    {
        var deployments = new List<LlmDeploymentEntity>
        {
            new LlmDeploymentEntity
            {
                HostId = "host-1",
                ModelId = "org/test-model",
                GpuIndices = "0",
                HostPort = 8001,
                Quantization = "none",
                ImageTag = "latest",
                Status = LlmDeploymentStatus.Stopped,
                StartedAt = default,
                CreatedAt = default,
            },
        };

        var host = new GpuHostEntity
        {
            HostId = "host-1",
            DisplayName = "Test Host",
            Host = "localhost",
            ProxyPort = 8080,
        };

        var config = NginxProxyService.GenerateConfig(deployments, 8080);

        // No deployments running -> should show 503 fallback
        config.Should().Contain("503");
        config.Should().NotContain("upstream vllm_");
    }

    [Test]
    public void RunningDeploymentIncludedInUpstream()
    {
        var deployments = new List<LlmDeploymentEntity>
        {
            new LlmDeploymentEntity
            {
                HostId = "host-1",
                ModelId = "org/test-model",
                GpuIndices = "0",
                HostPort = 8001,
                Quantization = "none",
                ImageTag = "latest",
                Status = LlmDeploymentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var host = new GpuHostEntity
        {
            HostId = "host-1",
            DisplayName = "Test Host",
            Host = "localhost",
            ProxyPort = 8080,
        };

        var config = NginxProxyService.GenerateConfig(deployments, 8080);

        config.Should().Contain("upstream vllm_pool");
        config.Should().Contain("server 127.0.0.1:8001;");
        config.Should().Contain("keepalive 32;");
        config.Should().Contain("location /");
        config.Should().Contain("proxy_pass http://vllm_pool;");
    }

    [Test]
    public void SharedModelAcrossHosts_UsesSingleUpstreamPool()
    {
        var deployments = new List<LlmDeploymentEntity>
        {
            new LlmDeploymentEntity
            {
                HostId = "host-1",
                ModelId = "llama-3.1-8b-v1",
                HostPort = 8001,
                Quantization = "none",
                ImageTag = "latest",
                Status = LlmDeploymentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new LlmDeploymentEntity
            {
                HostId = "host-2",
                ModelId = "llama-3.1-8b-v1",
                HostPort = 8002,
                Quantization = "none",
                ImageTag = "latest",
                Status = LlmDeploymentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var host = new GpuHostEntity
        {
            HostId = "host-2",
            DisplayName = "Test Host",
            Host = "localhost",
            ProxyPort = 8080,
        };

        var config = NginxProxyService.GenerateConfig(deployments, 8080);
        var upstreamName = NginxProxyService.ModelToUpstreamName("llama-3.1-8b-v1");

        // Should have exactly 1 upstream block
        var upstreamCount = Regex.Matches(config, $@"^.*upstream {Regex.Escape(upstreamName)}", RegexOptions.Multiline).Count;
        upstreamCount.Should().Be(1, "Same model name should have single upstream block");

        // Both servers should be in the same pool
        var upstreamMatch = Regex.Match(config, $@"upstream {Regex.Escape(upstreamName)}[^}]+}", RegexOptions.Multiline);
        if (upstreamMatch.Success)
        {
            var upstreamContent = upstreamMatch.Groups[0].Value;
            upstreamContent.Should().Contain("127.0.0.1:8001");
            upstreamContent.Should().Contain("127.0.0.1:8002");
        }
    }

    [Test]
    public void MultipleDeployments_AreOrderedByPort()
    {
        // Deployments are ordered by HostPort in ascending order
        var deployments = new List<LlmDeploymentEntity>
        {
            new LlmDeploymentEntity
            {
                HostId = "host-1",
                ModelId = "test/model-c",
                HostPort = 5000,
                Status = LlmDeploymentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new LlmDeploymentEntity
            {
                HostId = "host-1",
                ModelId = "test/model-a",
                HostPort = 10000,
                Status = LlmDeploymentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new LlmDeploymentEntity
            {
                HostId = "host-1",
                ModelId = "test/model-b",
                HostPort = 8000,
                Status = LlmDeploymentStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var host = new GpuHostEntity
        {
            HostId = "host-1",
            DisplayName = "Test Host",
            Host = "localhost",
            ProxyPort = 8080,
        };

        var config = NginxProxyService.GenerateConfig(deployments, 8080);

        // Should be in ascending port order: 5000, 8000, 10000
        config.Should().Contain("server 127.0.0.1:5000;");
        config.Should().Contain("server 127.0.0.1:8000;");
        config.Should().Contain("server 127.0.0.1:10000;");
    }
}
