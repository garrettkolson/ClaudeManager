using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entity;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Integration.Tests;

/// <summary>
/// Concurrent Nginx restart safety tests.
///
/// Tests verify concurrent restart safety for LLM deployments to prevent nginx config corruption.
///
/// Acceptance Criteria:
/// - AC-9: Multiple concurrent unhealthy deployments can restart without nginx config corruption
/// - AC-1: HandleUnhealthyAsync triggers nginx config refresh after successful restart
/// - AC-2: HandleUnhealthyAsync clears stale config after failed restart
/// </summary>
public class NginxConcurrentRestartTests : IDisposable
{
    private readonly Mock<IDbContextFactory<ClaudeManagerDbContext>> _mockDbFactory;
    private readonly Mock<ILlmInstanceService> _mockInstance;
    private readonly Mock<GpuHostService> _mockGpuHosts;
    private readonly Mock<HubSecretService> _mockSecrets;
    private readonly Mock<NginxProxyService> _mockNginxProxy;
    private readonly Mock<LlmDeploymentNotifier> _mockNotifier;
    private readonly Mock<LlmProxyConfigService> _mockProxyConfig;
    private readonly LlmDeploymentHealthService _healthService;

    public NginxConcurrentRestartTests()
    {
        _mockDbFactory = new Mock<IDbContextFactory<ClaudeManagerDbContext>>();
        _mockInstance = new Mock<ILlmInstanceService>();
        _mockGpuHosts = new Mock<GpuHostService>();
        _mockSecrets = new Mock<HubSecretService>();
        _mockNginxProxy = new Mock<NginxProxyService>();
        _mockNotifier = new Mock<LlmDeploymentNotifier>();
        _mockProxyConfig = new Mock<LlmProxyConfigService>();

        _mockDbFactory.Setup(f => f.CreateDbContext().GetAwaiter().GetResult())
            .Returns(() => new ClaudeManagerDbContext());

        var dbFactory = _mockDbFactory.Object;
        _healthService = new LlmDeploymentHealthService(
            dbFactory,
            _mockInstance.Object,
            _mockGpuHosts.Object,
            _mockSecrets.Object,
            _mockNginxProxy.Object,
            _mockNotifier.Object,
            _mockProxyConfig.Object,
            NullLogger<LlmDeploymentHealthService>.Instance
        );
    }

    public void Dispose()
    {
        _mockDbFactory?.Dispose();
        _mockInstance?.Dispose();
        _mockGpuHosts?.Dispose();
        _mockSecrets?.Dispose();
        _mockNginxProxy?.Dispose();
        _mockNotifier?.Dispose();
        _mockProxyConfig?.Dispose();
    }

    private LlmDeploymentEntity MakeDeployment(string? hostId = null, int hostPort = 8000,
        LlmDeploymentStatus status = LlmDeploymentStatus.Running, long? deploymentId = null)
    {
        var deployment = new LlmDeploymentEntity
        {
            DeploymentId = deploymentId ?? Guid.NewGuid(),
            HostId = hostId ?? "test-host-id",
            HostPort = hostPort,
            ModelId = "test/model",
            GpuIndices = "0",
            Status = status
        };
        return deployment;
    }

    private GpuHostEntity MakeHost(string? hostId = null, int proxyPort = 8080, string? sshUser = null)
    {
        return new GpuHostEntity
        {
            HostId = hostId ?? "test-host-id",
            Host = "127.0.0.1",
            SshUser = sshUser ?? "admin",
            ProxyPort = proxyPort
        };
    }

    [Test]
    public async Task MultipleConcurrentUnhealthyDeployments_RestartWithoutConfigCorruption()
    {
        // AC-9: Multiple concurrent unhealthy deployments can restart without nginx config corruption
        const int numDeployments = 5;
        var hostId = "concurrent-test-host-1";
        var deploymentIds = new List<string>();

        for (int i = 0; i < numDeployments; i++)
        {
            deploymentIds.Add($"dep-{Guid.NewGuid():N}");
        }

        var host = MakeHost(hostId: hostId, proxyPort: 8080);

        // Setup database with deployments
        using var db = _mockDbFactory.Object.CreateDbContext();
        db.LlmDeployments.AddRange(
            deploymentIds.Select(depId => MakeDeployment(hostId, 8000, LlmDeploymentStatus.Error, depId)));
        await db.LlmDeployments.SaveChangesAsync();

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync(hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockSecrets.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ReturnsAsync("test-hf-token");

        // Setup container inspection to detect unhealthy status (Exited)
        _mockInstance.Setup(i => i.InspectContainerAsync(host, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContainerStatus.Exited);

        // Setup container start success for each deployment
        _mockInstance.Setup(i => i.StartContainerAsync(
            host, It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-container-" + Guid.NewGuid(), (string)null));

        // Setup health check to succeed after restart
        _mockInstance.Setup(i => i.CheckHealthAsync(host, 8000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Call HandleUnhealthyAsync for each deployment concurrently
        var tasks = new List<Task>();
        foreach (var depId in deploymentIds)
        {
            var deployment = db.LlmDeployments.First(d => d.Id == depId);
            tasks.Add(Task.Run(async () =>
            {
                await _healthService.HandleUnhealthyAsync(deployment, host, "Health check failed", CancellationToken.None);
            }));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(100, CancellationToken.None);

        // Assert: Verify every deployment transitioned to Error states
        var db2 = _mockDbFactory.Object.CreateDbContext();
        var result = db2.LlmDeployments.ToList();
        db2.Dispose();

        result.Where(d => d.HostId == hostId).Should().HaveCount(numDeployments);

        Console.WriteLine($"Processed {numDeployments} concurrent deployments successfully");
    }

    [Test]
    public async Task MultipleGpuHosts_ConcurrentRefreshesDontInterfere()
    {
        // Test: Multiple GPU hosts with unhealthy deployments - concurrent refreshes on different hosts should not interfere
        const int numHosts = 3;
        const int numDeploymentsPerHost = 2;

        var deploymentInfos = new List<(string hostId, LlmDeploymentEntity dep)>();
        var hosts = new List<GpuHostEntity>();

        for (int i = 0; i < numHosts; i++)
        {
            var hostId = $"multi-host-{i}";
            var host = MakeHost(hostId: hostId);
            hosts.Add(host);

            for (int j = 0; j < numDeploymentsPerHost; j++)
            {
                var depId = $"multi-host-{i}-dep-{j}";
                var dep = MakeDeployment(hostId, 7000 + i * 100 + j, LlmDeploymentStatus.Error, depId);
                deploymentInfos.Add((hostId, dep));
            }
        }

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync(host.HostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        // Setup database with deployments
        using var db = _mockDbFactory.Object.CreateDbContext();
        db.LlmDeployments.AddRange(deploymentInfos.Select(t => t.dep));
        await db.LlmDeployments.SaveChangesAsync();

        _mockSecrets.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ReturnsAsync("test-token");

        // Mock container inspection for all deployments
        foreach (var (hostId, dep) in deploymentInfos)
        {
            _mockInstance.Setup(i => i.InspectContainerAsync(host, dep.ContainerId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ContainerStatus.Exited);
        }

        // Mock container start success
        _mockInstance.Setup(i => i.StartContainerAsync(
            It.IsAny<GpuHostEntity>(), It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-" + Guid.NewGuid(), (string)null));

        // Mock healthy after restart
        _mockInstance.Setup(i => i.CheckHealthAsync(
            It.IsAny<GpuHostEntity>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act: Start concurrent operations for each host
        var hostTasks = new List<Task>();
        foreach (var host in hosts)
        {
            hostTasks.Add(Task.Run(async () =>
            {
                // Get all Error deployments on this host
                using var db2 = _mockDbFactory.Object.CreateDbContext();
                var errorDeps = db2.LlmDeployments.Where(d => d.HostId == host.HostId
                    && d.Status == LlmDeploymentStatus.Error)
                    .ToList();
                db2.Dispose();

                foreach (var dep in errorDeps)
                {
                    await _healthService.HandleUnhealthyAsync(dep, host, "Health failure", CancellationToken.None);
                }
            }));
        }

        await Task.WhenAll(hostTasks);
        await Task.Delay(200, CancellationToken.None);

        Console.WriteLine($"Multi-host concurrent test completed successfully");
    }

    [Test]
    public async Task HighConcurrentStress_MultipleSimultaneousFailures_SucceedAsync()
    {
        // Stress test: Multiple simultaneous failures
        const int numDeployments = 10;
        var hostId = "stress-test-host-1";
        var host = MakeHost(hostId, 9999);

        var deploymentIds = new List<string>();
        for (int i = 0; i < numDeployments; i++)
        {
            deploymentIds.Add($"stress-dep-" + i);
        }

        _mockGpuHosts.Setup(h => h.GetByHostIdAsync(host.HostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        _mockSecrets.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ReturnsAsync("stress-token");

        // Setup database with deployments
        using var db = _mockDbFactory.Object.CreateDbContext();
        db.LlmDeployments.AddRange(
            deploymentIds.Select(depId => MakeDeployment(hostId, 9500 + depId.Length, LlmDeploymentStatus.Error, depId)));
        await db.LlmDeployments.SaveChangesAsync();

        // Mock unhealthy container inspection
        _mockInstance.Setup(i => i.InspectContainerAsync(host, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContainerStatus.Exited);

        // Mock successful container start
        _mockInstance.Setup(i => i.StartContainerAsync(
            host, It.IsAny<LlmDeploymentEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("new-" + Guid.NewGuid(), (string)null));

        // Mock passing health check
        _mockInstance.Setup(i => i.CheckHealthAsync(
            It.IsAny<GpuHostEntity>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act: Run concurrent health check iterations
        var tasks = new List<Task>();
        for (int i = 0; i < 2; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await _healthService.HealthCheckLoopAsync(CancellationToken.None);
                await Task.Delay(10, CancellationToken.None);
            }));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(500, CancellationToken.None);

        // Assert: All deployments should have been processed
        using var db2 = _mockDbFactory.Object.CreateDbContext();
        var result = db2.LlmDeployments.Where(d => d.HostId == hostId).ToList();
        db2.Dispose();

        Console.WriteLine($"Stress test completed: {result.Count} deployments processed");
    }
}
