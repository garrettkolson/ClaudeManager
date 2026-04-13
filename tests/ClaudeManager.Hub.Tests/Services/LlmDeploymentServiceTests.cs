using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Services.Docker;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class LlmDeploymentServiceTests
{
    private SqliteConnection        _conn       = default!;
    private GpuHostService          _gpuHosts   = default!;
    private HubSecretService        _secrets    = default!;
    private LlmDeploymentNotifier   _notifier   = default!;
    private Mock<LlmInstanceService> _instanceMock = default!;
    private Mock<IDockerExecutor>    _executorMock = default!;
    private LlmDeploymentService    _svc        = default!;

    [SetUp]
    public async Task SetUp()
    {
        var (factory, conn) = await InMemoryDbHelper.CreateAsync();
        _conn         = conn;
        _gpuHosts     = new GpuHostService(factory);
        _secrets      = new HubSecretService(factory);
        _notifier     = new LlmDeploymentNotifier();
        _instanceMock = new Mock<LlmInstanceService>(NullLogger<LlmInstanceService>.Instance, new HttpClient());
        _executorMock = new Mock<IDockerExecutor>();

        var nginxProxy = new NginxProxyService(NullLogger<NginxProxyService>.Instance, _executorMock.Object);
        var proxyConfigSvc = new LlmProxyConfigService(
            _gpuHosts, factory, new Mock<IHubContext<AgentHub>>().Object,
            NullLogger<LlmProxyConfigService>.Instance);

        _svc = new LlmDeploymentService(
            factory, _instanceMock.Object, _gpuHosts, _secrets, _notifier, nginxProxy, proxyConfigSvc,
            NullLogger<LlmDeploymentService>.Instance);
    }

    [TearDown]
    public void TearDown() => _conn.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<GpuHostEntity> SeedHostAsync(string hostId = "gpu-h1")
    {
        var host = new GpuHostEntity
        {
            HostId      = hostId,
            DisplayName = "Test GPU Host",
            Host        = "192.168.1.50",
            SshPort     = 22,
            SshUser     = "ubuntu",
            SshKeyPath  = "~/.ssh/id_rsa",
        };
        return await _gpuHosts.AddAsync(host);
    }

    private Task<LlmDeploymentEntity> SeedDeploymentAsync(
        string hostId = "gpu-h1", string modelId = "meta-llama/Llama-3.1-8B-Instruct") =>
        _svc.CreateAsync(hostId, modelId, gpuIndices: "0", hostPort: 8001);

    // ── GetAllAsync ────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAllAsync_EmptyDb_ReturnsEmpty()
    {
        (await _svc.GetAllAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllDeployments()
    {
        await SeedDeploymentAsync(modelId: "model/A");
        await SeedDeploymentAsync(modelId: "model/B");

        (await _svc.GetAllAsync()).Should().HaveCount(2);
    }

    // ── CreateAsync ────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_SetsDefaultStatus_Stopped()
    {
        var d = await SeedDeploymentAsync();

        d.Status.Should().Be(LlmDeploymentStatus.Stopped);
    }

    [Test]
    public async Task CreateAsync_AssignsDeploymentId()
    {
        var d = await SeedDeploymentAsync();

        d.DeploymentId.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task CreateAsync_SetsCreatedAt()
    {
        var before = DateTimeOffset.UtcNow;
        var d = await SeedDeploymentAsync();
        var after = DateTimeOffset.UtcNow;

        d.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Test]
    public async Task CreateAsync_PersistsAllFields()
    {
        var d = await _svc.CreateAsync(
            hostId:          "gpu-h1",
            modelId:         "mistral/Mistral-7B-v0.1",
            gpuIndices:      "0,1",
            hostPort:        8002,
            quantization:    "awq",
            extraArgs:       "--max-model-len 4096",
            hfTokenOverride: "hf_override");

        d.HostId.Should().Be("gpu-h1");
        d.ModelId.Should().Be("mistral/Mistral-7B-v0.1");
        d.GpuIndices.Should().Be("0,1");
        d.HostPort.Should().Be(8002);
        d.Quantization.Should().Be("awq");
        d.ExtraArgs.Should().Be("--max-model-len 4096");
        d.HfTokenOverride.Should().Be("hf_override");
    }

    [Test]
    public async Task CreateAsync_BlankExtraArgs_StoredAsNull()
    {
        var d = await _svc.CreateAsync("h", "m", "0", 8000, extraArgs: "   ");
        d.ExtraArgs.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_BlankHfOverride_StoredAsNull()
    {
        var d = await _svc.CreateAsync("h", "m", "0", 8000, hfTokenOverride: "  ");
        d.HfTokenOverride.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_DefaultImageTag_IsLatest()
    {
        var d = await SeedDeploymentAsync();
        d.ImageTag.Should().Be("latest");
    }

    [Test]
    public async Task CreateAsync_NightlyImageTag_Persisted()
    {
        var d = await _svc.CreateAsync("h", "m", "0", 8000, imageTag: "nightly");
        d.ImageTag.Should().Be("nightly");
    }

    [Test]
    public async Task CreateAsync_BlankImageTag_FallsBackToLatest()
    {
        var d = await _svc.CreateAsync("h", "m", "0", 8000, imageTag: "  ");
        d.ImageTag.Should().Be("latest");
    }

    // ── DeleteAsync ────────────────────────────────────────────────────────────

    [Test]
    public async Task DeleteAsync_RemovesDeployment()
    {
        var d = await SeedDeploymentAsync();

        await _svc.DeleteAsync(d.Id);

        (await _svc.GetAllAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task DeleteAsync_NonExistentId_DoesNotThrow()
    {
        await _svc.Invoking(s => s.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    // ── StartAsync ─────────────────────────────────────────────────────────────

    [Test]
    public async Task StartAsync_UnknownDeployment_ReturnsError()
    {
        var error = await _svc.StartAsync(999);

        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("not found");
    }

    [Test]
    public async Task StartAsync_UnknownHost_ReturnsError()
    {
        var d = await SeedDeploymentAsync(hostId: "nonexistent-host");

        var error = await _svc.StartAsync(d.Id);

        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("nonexistent-host");
    }

    [Test]
    public async Task StartAsync_DockerSucceeds_StatusBecomesRunning()
    {
        await SeedHostAsync("gpu-h1");
        var d = await SeedDeploymentAsync("gpu-h1");

        _instanceMock
            .Setup(m => m.StartContainerAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("abc123containerId", null));

        // Mock health check to pass immediately
        _instanceMock
            .Setup(m => m.CheckHealthAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var error = await _svc.StartAsync(d.Id);

        error.Should().BeNull();
        var all = await _svc.GetAllAsync();
        all[0].Status.Should().Be(LlmDeploymentStatus.Running);
        all[0].ContainerId.Should().Be("abc123containerId");
    }

    [Test]
    public async Task StartAsync_DockerFails_StatusBecomesError()
    {
        await SeedHostAsync("gpu-h1");
        var d = await SeedDeploymentAsync("gpu-h1");

        _instanceMock
            .Setup(m => m.StartContainerAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((null, "docker: image not found"));

        var error = await _svc.StartAsync(d.Id);

        error.Should().Contain("docker: image not found");
        var all = await _svc.GetAllAsync();
        all[0].Status.Should().Be(LlmDeploymentStatus.Error);
    }

    [Test]
    public async Task StartAsync_ContainerExitsImmediately_StatusBecomesError()
    {
        await SeedHostAsync("gpu-h1");
        var d = await SeedDeploymentAsync("gpu-h1");

        _instanceMock
            .Setup(m => m.StartContainerAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("cid", null));

        _instanceMock
            .Setup(m => m.CheckHealthAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _instanceMock
            .Setup(m => m.InspectContainerAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContainerStatus?)null); // container not found

        var error = await _svc.StartAsync(d.Id);

        error.Should().NotBeNullOrWhiteSpace();
        var all = await _svc.GetAllAsync();
        all[0].Status.Should().Be(LlmDeploymentStatus.Error);
        all[0].ContainerId.Should().BeNull();
    }

    [Test]
    public async Task StartAsync_ContainerIdPersistedBeforeHealthResolves()
    {
        await SeedHostAsync("gpu-h1");
        var d = await SeedDeploymentAsync("gpu-h1");

        _instanceMock
            .Setup(m => m.StartContainerAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(("cid_persisted", null));

        // Health passes immediately so the test completes without waiting 30s
        _instanceMock
            .Setup(m => m.CheckHealthAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _svc.StartAsync(d.Id);

        // Whether fast-healthy or slow-loading, ContainerId must always be in the DB
        var all = await _svc.GetAllAsync();
        all[0].ContainerId.Should().Be("cid_persisted");
    }

    [Test]
    public async Task StartAsync_UsesHfTokenOverride_WhenSet()
    {
        await SeedHostAsync("gpu-h1");
        await _secrets.SetAsync(HubSecretService.HuggingFaceTokenKey, "hf_global");
        var d = await _svc.CreateAsync("gpu-h1", "model/m", "0", 8001, hfTokenOverride: "hf_override");

        string? capturedToken = null;
        _instanceMock
            .Setup(m => m.StartContainerAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<GpuHostEntity, LlmDeploymentEntity, string?, CancellationToken>(
                (_, _, token, _) => capturedToken = token)
            .ReturnsAsync(("cid", null));

        // Mock health check to pass immediately
        _instanceMock
            .Setup(m => m.CheckHealthAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _svc.StartAsync(d.Id);

        capturedToken.Should().Be("hf_override");
    }

    [Test]
    public async Task StartAsync_UsesGlobalToken_WhenNoOverride()
    {
        await SeedHostAsync("gpu-h1");
        await _secrets.SetAsync(HubSecretService.HuggingFaceTokenKey, "hf_global");
        var d = await SeedDeploymentAsync("gpu-h1");

        string? capturedToken = null;
        _instanceMock
            .Setup(m => m.StartContainerAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<GpuHostEntity, LlmDeploymentEntity, string?, CancellationToken>(
                (_, _, token, _) => capturedToken = token)
            .ReturnsAsync(("cid", null));

        // Mock health check to pass immediately
        _instanceMock
            .Setup(m => m.CheckHealthAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _svc.StartAsync(d.Id);

        capturedToken.Should().Be("hf_global");
    }

    // ── StopAsync ──────────────────────────────────────────────────────────────

    [Test]
    public async Task StopAsync_UnknownDeployment_ReturnsError()
    {
        var error = await _svc.StopAsync(999);
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task StopAsync_NoContainerId_SetsStoppedWithoutCallingDocker()
    {
        var d = await SeedDeploymentAsync();

        var error = await _svc.StopAsync(d.Id);

        error.Should().BeNull();
        _instanceMock.Verify(
            m => m.StopContainerAsync(It.IsAny<GpuHostEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task StopAsync_DockerSucceeds_StatusBecomesStopped()
    {
        await SeedHostAsync("gpu-h1");
        var d = await SeedDeploymentAsync("gpu-h1");

        // Set up as running
        _instanceMock
            .Setup(m => m.StartContainerAsync(It.IsAny<GpuHostEntity>(), It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("cid123", null));

        // Mock health check to pass immediately
        _instanceMock
            .Setup(m => m.CheckHealthAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _svc.StartAsync(d.Id);

        _instanceMock
            .Setup(m => m.StopContainerAsync(It.IsAny<GpuHostEntity>(), "cid123", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var error = await _svc.StopAsync(d.Id);

        error.Should().BeNull();
        var all = await _svc.GetAllAsync();
        all[0].Status.Should().Be(LlmDeploymentStatus.Stopped);
        all[0].ContainerId.Should().BeNull();
    }

    // ── Notifier ───────────────────────────────────────────────────────────────

    [Test]
    public async Task StartAsync_DockerSucceeds_NotifierFires()
    {
        await SeedHostAsync("gpu-h1");
        var d = await SeedDeploymentAsync("gpu-h1");

        _instanceMock
            .Setup(m => m.StartContainerAsync(It.IsAny<GpuHostEntity>(), It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("cid", null));

        // Mock health check to pass immediately
        _instanceMock
            .Setup(m => m.CheckHealthAsync(
                It.IsAny<GpuHostEntity>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        LlmDeploymentEntity? notified = null;
        _notifier.DeploymentChanged += e => notified = e;

        await _svc.StartAsync(d.Id);

        notified.Should().NotBeNull();
        notified!.Status.Should().Be(LlmDeploymentStatus.Running);
    }

    // ── DetectContainersAsync ─────────────────────────────────────────────────

    [Test]
    public async Task DetectContainersAsync_InstanceReturnsError_BubblesError()
    {
        var host = await SeedHostAsync("gpu-h1");

        _instanceMock
            .Setup(m => m.ListRunningVllmContainersAsync(host, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<RawVllmContainerInfo>(), "SSH connection refused"));

        var (containers, error) = await _svc.DetectContainersAsync(host);

        containers.Should().BeEmpty();
        error.Should().Be("SSH connection refused");
    }

    [Test]
    public async Task DetectContainersAsync_NoContainers_ReturnsEmptyList()
    {
        var host = await SeedHostAsync("gpu-h1");

        _instanceMock
            .Setup(m => m.ListRunningVllmContainersAsync(host, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<RawVllmContainerInfo>(), null));

        var (containers, error) = await _svc.DetectContainersAsync(host);

        error.Should().BeNull();
        containers.Should().BeEmpty();
    }

    [Test]
    public async Task DetectContainersAsync_UnknownContainer_MarkedNotTracked()
    {
        var host = await SeedHostAsync("gpu-h1");
        var raw = new RawVllmContainerInfo("abc123", "latest", "model/m", 8001, "0", null, null);

        _instanceMock
            .Setup(m => m.ListRunningVllmContainersAsync(host, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<RawVllmContainerInfo> { raw }, null));

        var (containers, error) = await _svc.DetectContainersAsync(host);

        error.Should().BeNull();
        containers.Should().HaveCount(1);
        containers[0].AlreadyTracked.Should().BeFalse();
        containers[0].ExistingDeploymentDbId.Should().BeNull();
        containers[0].ContainerId.Should().Be("abc123");
    }

    [Test]
    public async Task DetectContainersAsync_KnownContainer_MarkedAlreadyTracked()
    {
        var host = await SeedHostAsync("gpu-h1");

        // Create a deployment and give it a ContainerId via start
        var d = await SeedDeploymentAsync("gpu-h1");
        _instanceMock
            .Setup(m => m.StartContainerAsync(It.IsAny<GpuHostEntity>(), It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("tracked-cid", null));
        _instanceMock
            .Setup(m => m.CheckHealthAsync(It.IsAny<GpuHostEntity>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await _svc.StartAsync(d.Id);

        var raw = new RawVllmContainerInfo("tracked-cid", "latest", "meta-llama/Llama-3.1-8B-Instruct", 8001, "0", null, null);
        _instanceMock
            .Setup(m => m.ListRunningVllmContainersAsync(host, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<RawVllmContainerInfo> { raw }, null));

        var (containers, error) = await _svc.DetectContainersAsync(host);

        error.Should().BeNull();
        containers.Should().HaveCount(1);
        containers[0].AlreadyTracked.Should().BeTrue();
        containers[0].ExistingDeploymentDbId.Should().Be(d.Id);
        containers[0].ExistingDeploymentId.Should().Be(d.DeploymentId);
    }

    [Test]
    public async Task DetectContainersAsync_MixedContainers_CorrectlyClassifiesBoth()
    {
        var host = await SeedHostAsync("gpu-h1");

        var d = await SeedDeploymentAsync("gpu-h1");
        _instanceMock
            .Setup(m => m.StartContainerAsync(It.IsAny<GpuHostEntity>(), It.IsAny<LlmDeploymentEntity>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("known-cid", null));
        _instanceMock
            .Setup(m => m.CheckHealthAsync(It.IsAny<GpuHostEntity>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await _svc.StartAsync(d.Id);

        var rawList = new List<RawVllmContainerInfo>
        {
            new("known-cid",   "latest",  "meta-llama/Llama-3.1-8B-Instruct", 8001, "0", null, null),
            new("unknown-cid", "nightly", "mistral/Mistral-7B-v0.1",           8002, "1", null, null),
        };
        _instanceMock
            .Setup(m => m.ListRunningVllmContainersAsync(host, It.IsAny<CancellationToken>()))
            .ReturnsAsync((rawList, null));

        var (containers, error) = await _svc.DetectContainersAsync(host);

        error.Should().BeNull();
        containers.Should().HaveCount(2);
        containers.Should().ContainSingle(c => c.ContainerId == "known-cid"   && c.AlreadyTracked);
        containers.Should().ContainSingle(c => c.ContainerId == "unknown-cid" && !c.AlreadyTracked);
    }

    // ── ImportDetectedAsync ───────────────────────────────────────────────────

    [Test]
    public async Task ImportDetectedAsync_AlreadyTracked_ReturnsError()
    {
        var detected = new DetectedVllmContainer(
            "cid", "latest", "model/m", 8001, "0", null, null,
            AlreadyTracked: true, ExistingDeploymentDbId: 1, ExistingDeploymentId: "dep-1");

        var (deployment, error) = await _svc.ImportDetectedAsync("gpu-h1", detected);

        error.Should().NotBeNullOrWhiteSpace();
        deployment.Should().BeNull();
    }

    [Test]
    public async Task ImportDetectedAsync_NewContainer_CreatesRunningDeployment()
    {
        var detected = new DetectedVllmContainer(
            "newcid", "latest", "meta-llama/Llama-3.1-8B-Instruct", 8001, "0,1", "awq", 32768,
            AlreadyTracked: false, ExistingDeploymentDbId: null, ExistingDeploymentId: null);

        var (deployment, error) = await _svc.ImportDetectedAsync("gpu-h1", detected);

        error.Should().BeNull();
        deployment.Should().NotBeNull();
        deployment.Status.Should().Be(LlmDeploymentStatus.Running);
        deployment.ContainerId.Should().Be("newcid");
        deployment.HostId.Should().Be("gpu-h1");
        deployment.ModelId.Should().Be("meta-llama/Llama-3.1-8B-Instruct");
        deployment.GpuIndices.Should().Be("0,1");
        deployment.HostPort.Should().Be(8001);
        deployment.Quantization.Should().Be("awq");
        deployment.MaxModelLen.Should().Be(32768);
        deployment.ImageTag.Should().Be("latest");
    }

    [Test]
    public async Task ImportDetectedAsync_NewContainer_GeneratesSlugDeploymentId()
    {
        var detected = new DetectedVllmContainer(
            "cid", "latest", "meta-llama/Llama-3.1-8B-Instruct", 8001, "0", null, null,
            AlreadyTracked: false, ExistingDeploymentDbId: null, ExistingDeploymentId: null);

        var (deployment, error) = await _svc.ImportDetectedAsync("gpu-h1", detected);

        error.Should().BeNull();
        deployment.DeploymentId.Should().StartWith("llama-3-");
        deployment.DeploymentId.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ImportDetectedAsync_NullModelId_UsesUnknownFallback()
    {
        var detected = new DetectedVllmContainer(
            "cid", "latest", null, null, null, null, null,
            AlreadyTracked: false, ExistingDeploymentDbId: null, ExistingDeploymentId: null);

        var (deployment, error) = await _svc.ImportDetectedAsync("gpu-h1", detected);

        error.Should().BeNull();
        deployment.ModelId.Should().Be("(unknown)");
        deployment.GpuIndices.Should().Be("0");
        deployment.HostPort.Should().Be(8000);
        deployment.Quantization.Should().Be("none");
    }

    [Test]
    public async Task ImportDetectedAsync_NewContainer_PersistedInDb()
    {
        var detected = new DetectedVllmContainer(
            "cid", "latest", "model/m", 8001, "0", null, null,
            AlreadyTracked: false, ExistingDeploymentDbId: null, ExistingDeploymentId: null);

        await _svc.ImportDetectedAsync("gpu-h1", detected);

        var all = await _svc.GetAllAsync();
        all.Should().HaveCount(1);
        all[0].ContainerId.Should().Be("cid");
        all[0].Status.Should().Be(LlmDeploymentStatus.Running);
    }

    [Test]
    public async Task ImportDetectedAsync_NewContainer_FiresNotifier()
    {
        var detected = new DetectedVllmContainer(
            "cid", "latest", "model/m", 8001, "0", null, null,
            AlreadyTracked: false, ExistingDeploymentDbId: null, ExistingDeploymentId: null);

        LlmDeploymentEntity? notified = null;
        _notifier.DeploymentChanged += e => notified = e;

        await _svc.ImportDetectedAsync("gpu-h1", detected);

        notified.Should().NotBeNull();
        notified!.Status.Should().Be(LlmDeploymentStatus.Running);
    }
}
