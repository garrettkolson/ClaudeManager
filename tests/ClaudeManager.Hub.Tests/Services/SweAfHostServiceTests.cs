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
