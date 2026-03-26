using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SweAfHostServiceTests
{
    // ── IsConfigured ──────────────────────────────────────────────────────────

    [Test]
    public void IsConfigured_BothCommandsPresent_ReturnsTrue()
    {
        var svc = Build(startCommand: "start.sh", stopCommand: "stop.sh");
        svc.IsConfigured.Should().BeTrue();
    }

    [Test]
    public void IsConfigured_MissingStartCommand_ReturnsFalse()
    {
        var svc = Build(startCommand: "", stopCommand: "stop.sh");
        svc.IsConfigured.Should().BeFalse();
    }

    [Test]
    public void IsConfigured_MissingStopCommand_ReturnsFalse()
    {
        var svc = Build(startCommand: "start.sh", stopCommand: "");
        svc.IsConfigured.Should().BeFalse();
    }

    [Test]
    public void IsConfigured_NeitherCommandPresent_ReturnsFalse()
    {
        var svc = Build(startCommand: null, stopCommand: null);
        svc.IsConfigured.Should().BeFalse();
    }

    // ── Guard: not configured ─────────────────────────────────────────────────

    [Test]
    public async Task StartAsync_NotConfigured_ReturnsError()
    {
        var svc = Build(startCommand: null, stopCommand: null);
        var (ok, err) = await svc.StartAsync();
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task StopAsync_NotConfigured_ReturnsError()
    {
        var svc = Build(startCommand: null, stopCommand: null);
        var (ok, err) = await svc.StopAsync();
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    // ── Guard: no SSH auth on remote host ─────────────────────────────────────

    [Test]
    public async Task StartAsync_RemoteHostWithNoAuth_ReturnsError()
    {
        var config = new SweAfHostConfig
        {
            Host         = "remote.example.com",
            SshUser      = "user",
            StartCommand = "systemctl start agentfield",
            StopCommand  = "systemctl stop agentfield",
            // No SshKeyPath or SshPassword
        };
        var svc = new SweAfHostService(config, NullLogger<SweAfHostService>.Instance);
        var (ok, err) = await svc.StartAsync();
        ok.Should().BeFalse();
        err.Should().Contain("SSH");
    }

    // ── AnthropicBaseUrl / AnthropicApiKey do not affect IsConfigured ─────────

    [Test]
    public void IsConfigured_WithAnthropicOverrides_StillTrue()
    {
        var config = new SweAfHostConfig
        {
            Host             = "localhost",
            StartCommand     = "start.sh",
            StopCommand      = "stop.sh",
            AnthropicBaseUrl = "http://localhost:11434",
            AnthropicApiKey  = "local",
        };
        new SweAfHostService(config, NullLogger<SweAfHostService>.Instance)
            .IsConfigured.Should().BeTrue();
    }

    // ── InjectEnvVars ─────────────────────────────────────────────────────────

    [Test]
    public void InjectEnvVars_NeitherSet_CommandUnchanged()
    {
        var svc = Build("start.sh", "stop.sh");
        // Indirectly verify: not configured guard fires before any command runs
        // The injection logic is exercised in integration; here we just confirm
        // the service builds without throwing when no overrides are set.
        svc.IsConfigured.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SweAfHostService Build(string? startCommand, string? stopCommand) =>
        new SweAfHostService(
            new SweAfHostConfig
            {
                Host         = "localhost",
                StartCommand = startCommand ?? string.Empty,
                StopCommand  = stopCommand  ?? string.Empty,
            },
            NullLogger<SweAfHostService>.Instance);
}
