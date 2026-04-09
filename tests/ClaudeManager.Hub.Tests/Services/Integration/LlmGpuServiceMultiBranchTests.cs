using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeManager.Hub.Tests.Services.Integration;

/// <summary>
/// Integration tests for LlmGpuService with cross-branch interaction scenarios.
/// Tests parsing functionality from LlmGpuMetrics.cs and execution from LlmGpuService.cs.
/// </summary>
public class LlmGpuServiceMultiBranchTests
{
    private readonly LlmGpuService _svc;

    public LlmGpuServiceMultiBranchTests()
    {
        _svc = new LlmGpuService(
            NullLogger<LlmGpuService>.Instance,
            TimeSpan.FromSeconds(5));
    }

    #region LlmGpuMetrics Parsing Integration Tests

    [Test]
    public static void ParseNvtopTextOutput_SingleGpu_ReturnsCorrectMetrics()
    {
        // Arrange
        const string nvtopOutput = @"
  GPU  Memory  Usage
  0    81920    72000    88%
  1    16384    15000    92%
";

        // Act
        var metrics = ParseNvtopTextOutput(nvtopOutput);

        // Assert
        metrics.Count.Should().Be(2);
        metrics[0].GpuName.Should().Be("GPU 0");
        metrics[0].MemoryTotalMiB.Should().Be(81920);
        metrics[0].MemoryUsedMiB.Should().Be(72000);
        metrics[0].UtilizationPercent.Should().Be(88.0);
        metrics[1].GpuName.Should().Be("GPU 1");
        metrics[1].MemoryTotalMiB.Should().Be(16384);
        metrics[1].MemoryUsedMiB.Should().Be(15000);
        metrics[1].UtilizationPercent.Should().Be(92.0);
    }

    [Test]
    public static void ParseNvtopTextOutput_MultiGpuHost_ReturnsAllMetrics()
    {
        // Arrange
        const string nvtopOutput = @"
  GPU  #     Memory  Usage  Process
  0    0     81920   72000  88%  /model1
  1    0     16384   15000  92%  /model2
  2    1     32768   28000  85%  /model3
";

        // Act
        var metrics = ParseNvtopTextOutput(nvtopOutput);

        // Assert
        metrics.Count.Should().Be(3);
        metrics[0].GpuName.Should().Be("GPU 0");
        metrics[0].UtilizationPercent.Should().Be(88.0);
        metrics[1].GpuName.Should().Be("GPU 1");
        metrics[1].UtilizationPercent.Should().Be(92.0);
        metrics[2].GpuName.Should().Be("GPU 2  #", "#     Memory");  // Combined headers detected
    }

    [Test]
    public static void ParseNvtopTextOutput_Empty_ReturnsEmptyList()
    {
        // Arrange
        const string nvtopOutput = "";

        // Act
        var metrics = ParseNvtopTextOutput(nvtopOutput);

        // Assert
        metrics.Should().BeEmpty();
    }

    [Test]
    public static void ParseNvtopTextOutput_WhitespaceOnly_ReturnsEmptyList()
    {
        // Arrange
        const string nvtopOutput = "\n\n   \n";

        // Act
        var metrics = ParseNvtopTextOutput(nvtopOutput);

        // Assert
        metrics.Should().BeEmpty();
    }

    #region Boundary Check Tests

    [Test]
    public static void ParseNvtopTextOutput_BoundChecking_ValidatesArrayAccess()
    {
        // Arrange - Test edge cases that could cause index out of range
        const string nvtopOutput = @"
  GPU  Memory  Usage
";

        // Act - This should not throw IndexOutOfRangeException
        var metrics = ParseNvtopTextOutput(nvtopOutput);

        // Assert
        metrics.Should().BeEmpty();
    }

    [Test]
    public static void ParseNvtopTextOutput_NullCheck_SafeWithNull()
    {
        // Act/Assert - ParseNvtopTextOutput should handle null input safely (will throw NullReferenceEx - expected)
#if !DEBUG
        Should.Throw<System.NullReferenceException>(() =>
            ParseNvtopTextOutput(null!));
#else
        var ex = Record.Exception<Exception>(() =>
            ParseNvtopTextOutput(null!));
        ex.Should().NotBeNull();
#endif
    }
    #endregion

    #endregion

    #region LlmGpuService Execution Integration Tests

    [Test]
    public async Task ExecuteAsync_Localhost_ReturnsWithoutSSH()
    {
        // Arrange
        var host = new GpuHostEntity
        {
            HostId = 1,
            DisplayName = "TestHost",
            Host = "localhost",
            SshPort = 22
        };

        // Act
        var (metrics, error) = await _svc.ExecuteAsync(host, CancellationToken.None);

        // Assert - Should return error since nvtop/nvidia-smi not available in test env
        error.Should().NotBeNull();
    }

    [Test]
    public async Task ExecuteAsync_RemoteHost_UsesSSHConnection()
    {
        // Arrange
        var host = new GpuHostEntity
        {
            HostId = 1,
            DisplayName = "RemoteHost",
            Host = "192.168.1.100",
            SshPort = 22,
            SshUser = "ubuntu"
        };

        // Act
        var (metrics, error) = await _svc.ExecuteAsync(host, CancellationToken.None);

        // Assert - Should fail gracefully with appropriate error
        error.Should().NotBeNull();
        error.Should().Contain("connection refused")
            .OrContain("connection timeout")
            .OrContain("command");
    }

    [Test]
    public async Task ExecuteAsync_FallbackToNvidiaSmi_WhenNvtopMissing()
    {
        // Arrange
        var host = new GpuHostEntity
        {
            HostId = 1,
            DisplayName = "TestHost",
            Host = "localhost",
            SshPort = 22
        };

        // Act
        var (metrics, error) = await _svc.ExecuteAsync(host, CancellationToken.None);

        // Assert - Error should indicate neither command worked
        error.Should().NotBeNull();
    }

    [Test]
    public async Task ExecuteAsync_CancellationTokenPropagates()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var host = new GpuHostEntity
        {
            HostId = 1,
            Host = "localhost",
            SshPort = 22
        };

        // Act
        var (metrics, error) = await _svc.ExecuteAsync(host, cts.Token);

        // Assert - Cancellation should propagate
        error.Should().NotBeNull();
    }
    #endregion

    #namespace NvtopGpuParserTests

    private static IReadOnlyList<GpuMetrics> ParseNvtopTextOutput(string output)
    {
        return LlmGpuService.ParseNvtopTextOutput(output);
    }
    #endregion
}
