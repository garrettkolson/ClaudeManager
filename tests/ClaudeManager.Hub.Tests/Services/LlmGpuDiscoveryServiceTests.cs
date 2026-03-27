using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class LlmGpuDiscoveryServiceTests
{
    private LlmGpuDiscoveryService _svc = default!;

    [SetUp]
    public void SetUp() =>
        _svc = new LlmGpuDiscoveryService(NullLogger<LlmGpuDiscoveryService>.Instance);

    // ── Parse — single GPU ────────────────────────────────────────────────────

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

    // ── Parse — multiple GPUs ─────────────────────────────────────────────────

    [Test]
    public void Parse_MultipleGpus_ReturnsAllEntries()
    {
        var output =
            "0, NVIDIA A100-SXM4-80GB, 81920, 79000\n" +
            "1, NVIDIA A100-SXM4-80GB, 81920, 78500\n" +
            "2, NVIDIA RTX 4090, 24564, 20000\n";

        var gpus = LlmGpuDiscoveryService.Parse(output);

        gpus.Should().HaveCount(3);
        gpus[0].Index.Should().Be(0);
        gpus[1].Index.Should().Be(1);
        gpus[2].Index.Should().Be(2);
        gpus[2].Name.Should().Be("NVIDIA RTX 4090");
        gpus[2].MemoryTotalMb.Should().Be(24564);
    }

    // ── Parse — whitespace handling ────────────────────────────────────────────

    [Test]
    public void Parse_ExtraWhitespaceAroundFields_Trimmed()
    {
        var output = "  0  ,  GeForce RTX 3090  ,  24576  ,  20000  \n";

        var gpus = LlmGpuDiscoveryService.Parse(output);

        gpus.Should().HaveCount(1);
        gpus[0].Name.Should().Be("GeForce RTX 3090");
        gpus[0].MemoryTotalMb.Should().Be(24576);
    }

    [Test]
    public void Parse_WindowsLineEndings_HandledCorrectly()
    {
        var output = "0, NVIDIA RTX 4090, 24564, 20000\r\n1, NVIDIA RTX 4090, 24564, 22000\r\n";

        var gpus = LlmGpuDiscoveryService.Parse(output);

        gpus.Should().HaveCount(2);
    }

    // ── Parse — empty / malformed input ──────────────────────────────────────

    [Test]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var gpus = LlmGpuDiscoveryService.Parse("");
        gpus.Should().BeEmpty();
    }

    [Test]
    public void Parse_WhitespaceOnly_ReturnsEmpty()
    {
        var gpus = LlmGpuDiscoveryService.Parse("   \n  \n  ");
        gpus.Should().BeEmpty();
    }

    [Test]
    public void Parse_MalformedLine_TooFewColumns_Skipped()
    {
        var output = "0, NVIDIA A100\n";  // only 2 columns

        var gpus = LlmGpuDiscoveryService.Parse(output);

        gpus.Should().BeEmpty();
    }

    [Test]
    public void Parse_NonNumericIndex_LineSkipped()
    {
        var output = "bad, NVIDIA A100, 81920, 79000\n";

        var gpus = LlmGpuDiscoveryService.Parse(output);

        gpus.Should().BeEmpty();
    }

    [Test]
    public void Parse_NonNumericMemory_LineSkipped()
    {
        var output = "0, NVIDIA A100, N/A, 79000\n";

        var gpus = LlmGpuDiscoveryService.Parse(output);

        gpus.Should().BeEmpty();
    }

    [Test]
    public void Parse_MixedValidAndMalformedLines_OnlyValidReturned()
    {
        var output =
            "0, NVIDIA A100, 81920, 79000\n" +
            "bad line\n" +
            "1, NVIDIA RTX 4090, 24564, 20000\n";

        var gpus = LlmGpuDiscoveryService.Parse(output);

        gpus.Should().HaveCount(2);
        gpus[0].Index.Should().Be(0);
        gpus[1].Index.Should().Be(1);
    }

    // ── DiscoverAsync — remote host, no auth configured ──────────────────────

    [Test]
    public async Task DiscoverAsync_RemoteHost_NoAuth_ReturnsError()
    {
        var host = new GpuHostEntity
        {
            Host        = "192.168.1.50",
            SshPort     = 22,
            SshUser     = "ubuntu",
            SshKeyPath  = null,
            SshPassword = null,
        };

        var (gpus, error) = await _svc.DiscoverAsync(host);

        gpus.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("SSH authentication");
    }

    // ── DiscoverAsync — cancellation ──────────────────────────────────────────

    [Test]
    public async Task DiscoverAsync_RemoteHost_CancelledToken_ReturnsError()
    {
        var host = new GpuHostEntity
        {
            Host       = "192.168.1.50",
            SshPort    = 22,
            SshUser    = "ubuntu",
            SshKeyPath = "~/.ssh/id_rsa",
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (gpus, error) = await _svc.DiscoverAsync(host, cts.Token);

        gpus.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
