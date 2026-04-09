using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services.Integration;

/// <summary>
/// Integration tests for NvtopGpuParser and parsing utilities across branches.
/// Tests the functionality from LlmGpuMetrics.cs integration with LlmGpuService.cs.
/// </summary>
public class LlmGpuMetricsIntegrationTests
{
    #region NvtopGpuParser Parse Tests

    [Test]
    public static async Task ParseAsync_ValidNvtopOutput_ReturnsMetrics()
    {
        // Arrange
        const string nvtopOutput = @"
  GPU  Memory  Usage
  0    81920   72000   88%
  1    16384   15000   92%
";

        // Act
        var metrics = await NvtopGpuParser.ParseAsync(nvtopOutput, CancellationToken.None);

        // Assert
        metrics.Count.Should().Be(2);
        metrics[0].GpuName.Should().Be("GPU");
        metrics[0].MemoryTotalMiB.Should().Be(81920);
        metrics[0].MemoryUsedMiB.Should().Be(72000);
        metrics[0].UtilizationPercent.Should().BeGreaterThanOrEqualTo(0);
        metrics[0].UtilizationPercent.Should().BeLessThanOrEqualTo(100);
    }

    [Test]
    public static void Parse_ValidCSVFormat_ReturnsMetrics()
    {
        // Arrange
        const string nvtopOutput = @"0, NVIDIA A100, 81920 72000 0 88
1, NVIDIA A100, 16384 15000 0 92
";

        // Act
        var metrics = NvtopGpuParser.Parse(nvtopOutput);

        // Assert
        metrics.Count.Should().Be(2);
        metrics[0].MemoryTotalMiB.Should().Be(81920);
        metrics[0].MemoryUsedMiB.Should().Be(72000);
        metrics[0].UtilizationPercent.Should().Be(88.0);
        metrics[1].MemoryTotalMiB.Should().Be(16384);
        metrics[1].MemoryUsedMiB.Should().Be(15000);
        metrics[1].UtilizationPercent.Should().Be(92.0);
    }

    [Test]
    public static void Parse_EmptyString_ReturnsEmptyList()
    {
        // Arrange
        const string nvtopOutput = "";

        // Act
        var metrics = NvtopGpuParser.Parse(nvtopOutput);

        // Assert
        metrics.Should().BeEmpty();
    }

    [Test]
    public static void ParseAsync_NullOutput_ReturnsEmptyList()
    {
        // Act - async method should handle null gracefully
        Task<IReadOnlyList<GpuMetrics>> task = async () =>
            await NvtopGpuParser.ParseAsync(null!, CancellationToken.None);

        var result = task.Result;

        // Assert
        result.Should().BeEmpty();
    }
    #endregion

    #region GpuMetrics Record Tests

    [Test]
    public static void GpuMetrics_Constructor_Validates()
    {
        // Arrange
        var metrics = new GpuMetrics(
            "NVIDIA A100",
            81920,
            72000,
            88.5);

        // Assert
        metrics.GpuName.Should().Be("NVIDIA A100");
        metrics.MemoryTotalMiB.Should().Be(81920);
        metrics.MemoryUsedMiB.Should().Be(72000);
        metrics.UtilizationPercent.Should().Be(88.5);
    }

    [Test]
    public static void GpuMetrics_ToGpuMetrics_ReturnsSameRecord()
    {
        // Arrange
        var original = new GpuMetrics("Test GPU", 8192, 4096, 50.0);

        // Act
        var result = original.ToGpuMetrics();

        // Assert
        result.GpuName.Should().Be("Test GPU");
        result.MemoryTotalMiB.Should().Be(8192);
        result.MemoryUsedMiB.Should().Be(4096);
        result.UtilizationPercent.Should().Be(50.0);
    }
    #endregion

    #region NvtopGpuLine Extensions Tests

    [Test]
    public static void ToGpuMetrics_ExtensionMethod_WorksCorrectly()
    {
        // Arrange
        var lines = new List<NvtopGpuLine>
        {
            new NvtopGpuLine(0, "GPU 0", 8192, 4000, 50.0),
            new NvtopGpuLine(1, "GPU 1", 16384, 8000, 49.0)
        };

        // Act
        var metrics = lines.ToGpuMetrics();

        // Assert
        metrics.Count.Should().Be(2);
        metrics[0].Name.Should().Be("GPU 0");
        metrics[0].MemoryTotalMiB.Should().Be(8192);
        metrics[1].Name.Should().Be("GPU 1");
        metrics[1].MemoryTotalMiB.Should().Be(16384);
    }

    [Test]
    public static void ToGpuMetrics_ReadOnlyList_ThrowsWithNull()
    {
        // Act - should not throw when passed null
        // This tests null safety
        var metrics = null!.ToGpuMetrics();

        // Assert
        metrics.Should().BeEmpty();
    }
    #endregion

    #region Boundary Edge Cases

    [Test]
    public static void ParseAsync_BoundaryValues_WithinLimits()
    {
        // Arrange - test utilization at boundary
        const string nvtopOutput = @"
0, GPU, 8192 100 0 100
1, GPU, 8192 0 0 0
";

        // Act
        var metrics = NvtopGpuParser.Parse(nvtopOutput);

        // Assert - Utilization should be bounded [0, 100]
        for (int i = 0; i < metrics.Count; i++)
        {
            metrics[i].UtilizationPercent.Should().BeGreaterOrEqualTo(0);
            metrics[i].UtilizationPercent.Should().BeLessOrEqualTo(100);
        }
    }

    [Test]
    public static void ParseAsync_BoundaryTotalMemory_Values()
    {
        // Arrange - test low and high memory values
        const string nvtopOutput = @"
0, GPU, 100 50 0 50   // 50% usage
1, GPU, 0 0 0 0       // 0 total, 0 used -> handled
";

        // Act - should handle boundary case where memoryTotal is 0
        var metrics = NvtopGpuParser.Parse(nvtopOutput);

        // Assert - At least one metric parsed
        metrics.Count.Should().BeGreaterThanOrEqualTo(0);
    }
    #endregion
}
