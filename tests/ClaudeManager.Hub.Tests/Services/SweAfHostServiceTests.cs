using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SweAfHostServiceTests
{
    // ── IsConfigured / Commands ───────────────────────────────────────────────

    [Test]
    public void IsConfigured_WithCommands_ReturnsTrue()
    {
        var svc = Build([new SweAfHostCommand { Label = "Start", Command = "start.sh" }]);
        svc.IsConfigured.Should().BeTrue();
    }

    [Test]
    public void IsConfigured_EmptyCommandList_ReturnsFalse()
    {
        var svc = Build([]);
        svc.IsConfigured.Should().BeFalse();
    }

    [Test]
    public void IsConfigured_NullCommandList_ReturnsFalse()
    {
        var svc = Build(null);
        svc.IsConfigured.Should().BeFalse();
    }

    [Test]
    public void Commands_ReflectsConfiguredList()
    {
        var cmds = new List<SweAfHostCommand>
        {
            new() { Label = "Start", Command = "start.sh" },
            new() { Label = "Stop",  Command = "stop.sh"  },
        };
        var svc = Build(cmds);
        svc.Commands.Should().HaveCount(2);
        svc.Commands[0].Label.Should().Be("Start");
        svc.Commands[1].Label.Should().Be("Stop");
    }

    [Test]
    public void Commands_NullConfig_ReturnsEmptyList()
    {
        var svc = Build(null);
        svc.Commands.Should().BeEmpty();
    }

    // ── Guard: not configured ─────────────────────────────────────────────────

    [Test]
    public async Task RunAsync_NotConfigured_ReturnsError()
    {
        var svc = Build(null);
        var (ok, err) = await svc.RunAsync("start.sh", "Start");
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    // ── Guard: no SSH auth on remote host ─────────────────────────────────────

    [Test]
    public async Task RunAsync_RemoteHostWithNoAuth_ReturnsError()
    {
        var config = new SweAfHostConfig
        {
            Host    = "remote.example.com",
            SshUser = "user",
            Commands =
            [
                new SweAfHostCommand { Label = "Start", Command = "start.sh" },
            ],
            // No SshKeyPath or SshPassword
        };
        var svc = new SweAfHostService(config, NullLogger<SweAfHostService>.Instance);
        var (ok, err) = await svc.RunAsync("start.sh", "Start");
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
            AnthropicBaseUrl = "http://localhost:11434",
            AnthropicApiKey  = "local",
            Commands =
            [
                new SweAfHostCommand { Label = "Start", Command = "start.sh" },
            ],
        };
        new SweAfHostService(config, NullLogger<SweAfHostService>.Instance)
            .IsConfigured.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SweAfHostService Build(IEnumerable<SweAfHostCommand>? commands) =>
        new SweAfHostService(
            new SweAfHostConfig
            {
                Host     = "localhost",
                Commands = commands?.ToList(),
            },
            NullLogger<SweAfHostService>.Instance);
}
