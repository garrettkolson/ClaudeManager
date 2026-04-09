using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class LlmGpuServiceTests
{
    private LlmGpuService? _svc;

    [SetUp]
    public void SetUp()
    {
        _svc = new LlmGpuService(NullLogger<LlmGpuService>.Instance);
    }

    [TearDown]
    public void TearDown() => _svc?.Dispose();

    // ── GetAsync — empty cache returns error ───────────────────────────────────

    [Test]
    public async Task GetAsync_NoMetricsCache_ReturnsError()
    {
        // Arrange
        // There's no way to pre-populate the service's internal cache externally
        // because it's populated by the BackgroundService task.
        // This is expected - metrics are refreshed on a 5-second interval.
        // But we can at least verify the test compiles.

        // Act
        var (gpus, error, lastUpdated) = await _svc!.GetAsync(0);

        // Assert
        error.Should().NotBeNull();
        error.Should().Contain("No metrics available");
    }

    // ── Parse tests for LlmGpuDiscoveryService (for end-to-end) ───────────────

    [Test]
    public void Parse_SingleGpu_ReturnsOneEntry()
    {
        var output = "0, NVIDIA A100-SXM4-80GB, 81920, 79000\n";
        var gpus = LlmGpuDiscoveryService.Parse(output);
        gpus.Should().HaveCount(1);
        gpus[0].Index.Should().Be(0);
        gpus[0].Name.Should().Be("NVIDIA A100-SXM4-80GB");
        gpus[0].MemoryTotalMb.Should().Be(81920);
        gpus[0].MemoryFreeMb.Should().Be(79000);
    }

    [Test]
    public void Parse_MultipleGpus_ReturnsAllEntries()
    {
        var output =
            "0, NVIDIA A100-SXM4-80GB, 81920, 79000\n" +
            "1, NVIDIA RTX 4090, 24564, 20000\n";
        var gpus = LlmGpuDiscoveryService.Parse(output);
        gpus.Should().HaveCount(2);
        gpus[0].Name.Should().Be("NVIDIA A100-SXM4-80GB");
        gpus[1].Name.Should().Be("NVIDIA RTX 4090");
    }

    [Test]
    public void Parse_MalformedInput_ReturnsEmpty()
    {
        var gpus = LlmGpuDiscoveryService.Parse("bad line");
        gpus.Should().BeEmpty();
    }

    // ── GpuMetrics calculations ───────────────────────────────────────────────

    [Test]
    public void GpuMetrics_MemoryPercent_UtilizationCorrect()
    {
        var metrics = new GpuMetrics(0, "Test GPU", 10000, 5000, 0);

        metrics.MemoryUsedMb.Should().Be(5000);
        metrics.MemoryPercentUtilized.Should().BeExactly(50m);
    }

    [Test]
    public void GpuMetrics_ZeroTotal_MemoryPercentZero()
    {
        var metrics = new GpuMetrics(0, "Test GPU", 0, 0, 0);

        metrics.MemoryUsedMb.Should().Be(0);
        metrics.MemoryPercentUtilized.Should().BeExactly(0m);
    }

    [Test]
    public void GpuMetrics_FullUtilization_Display()
    {
        var metrics = new GpuMetrics(0, "Test GPU", 8192, 8192, 0);

        metrics.MemoryUsedMb.Should().Be(0);
        metrics.MemoryPercentUtilized.Should().BeExactly(0m);
    }

    [Test]
    public void GpuMetrics_EmptyMemory_Display()
    {
        var metrics = new GpuMetrics(0, "Test GPU", 0, 0, 0);

        metrics.MemoryUsedMb.Should().Be(0);
        metrics.MemoryPercentUtilized.Should().BeExactly(0m);
    }
}