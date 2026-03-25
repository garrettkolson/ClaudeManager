using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class AgentLaunchServiceTests
{
    private static KnownMachineConfig MakeConfig(string machineId) => new()
    {
        MachineId    = machineId,
        DisplayName  = "Test Box",
        Platform     = "linux",
        Host         = "192.168.1.10",
        Port         = 22,
        SshUser      = "user",
        SshKeyPath   = "~/.ssh/id_rsa",
        AgentCommand = "nohup ./agent &",
    };

    private static AgentLaunchService Build(params KnownMachineConfig[] configs) =>
        new(configs, NullLogger<AgentLaunchService>.Instance);

    // ── HasLaunchConfig ───────────────────────────────────────────────────────

    [Test]
    public void HasLaunchConfig_KnownMachineId_ReturnsTrue()
    {
        var svc = Build(MakeConfig("m1"), MakeConfig("m2"));

        svc.HasLaunchConfig("m1").Should().BeTrue();
        svc.HasLaunchConfig("m2").Should().BeTrue();
    }

    [Test]
    public void HasLaunchConfig_UnknownMachineId_ReturnsFalse()
    {
        var svc = Build(MakeConfig("m1"));

        svc.HasLaunchConfig("unknown").Should().BeFalse();
    }

    [Test]
    public void HasLaunchConfig_EmptyConfigList_ReturnsFalse()
    {
        var svc = Build();

        svc.HasLaunchConfig("anything").Should().BeFalse();
    }

    // ── LaunchAgentAsync (error paths only — process/SSH not exercised) ───────

    [Test]
    public async Task LaunchAgentAsync_UnknownMachineId_ReturnsFalseWithError()
    {
        var svc = Build(MakeConfig("m1"));

        var (success, error) = await svc.LaunchAgentAsync("unknown");

        success.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task LaunchAgentAsync_NoSshCredentials_ReturnsFalseWithError()
    {
        // Remote machine but no SSH key or password
        var config = new KnownMachineConfig
        {
            MachineId    = "remote-m1",
            DisplayName  = "Remote",
            Platform     = "linux",
            Host         = "10.0.0.1",     // not localhost
            Port         = 22,
            SshUser      = "user",
            SshKeyPath   = null,
            SshPassword  = null,           // no auth configured
            AgentCommand = "nohup ./agent &",
        };
        var svc = new AgentLaunchService([config], NullLogger<AgentLaunchService>.Instance);

        var (success, error) = await svc.LaunchAgentAsync("remote-m1");

        success.Should().BeFalse();
        error.Should().Contain("SSH authentication");
    }
}
