using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

/// <summary>
/// Tests for <see cref="SweAfProvisioningService.BuildWriteHubPortOverrideCommand"/>.
/// Validates the generated docker-compose.hub.yml YAML that assigns per-build host ports
/// and injects Docker service-DNS environment variables for agent discovery.
/// </summary>
[TestFixture]
public class SweAfProvisioningHubOverrideTests
{
    private const string RepoPath = "/home/user/swe-af";

    private static string DecodeYaml(string cmd)
    {
        // cmd format: echo {b64} | base64 -d > {path}
        var pipeIdx = cmd.IndexOf(" | base64 -d > ");
        pipeIdx.Should().NotBe(-1, "command must be base64-encoded");
        var b64 = cmd["echo ".Length..pipeIdx].Trim();
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    // ── Command structure ─────────────────────────────────────────────────────

    [Test]
    public void Command_WritesToHubYmlInRepoPath()
    {
        var cmd = SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102);
        cmd.Should().EndWith($"{RepoPath}/docker-compose.hub.yml");
    }

    [Test]
    public void Command_IsBase64Encoded()
    {
        var cmd = SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102);
        cmd.Should().StartWith("echo ");
        cmd.Should().Contain(" | base64 -d > ");
    }

    // ── Port mapping ──────────────────────────────────────────────────────────

    [Test]
    public void Yaml_ControlPlane_MapsCorrectHostPort()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 9000, 9001, 9002));
        yaml.Should().Contain("\"9000:8080\"");
    }

    [Test]
    public void Yaml_SweAgent_MapsCorrectHostPort()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 9000, 9001, 9002));
        yaml.Should().Contain("\"9001:8003\"");
    }

    [Test]
    public void Yaml_SweFast_MapsCorrectHostPort()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 9000, 9001, 9002));
        yaml.Should().Contain("\"9002:8004\"");
    }

    [Test]
    public void Yaml_PortsUseOverrideDirective()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102));
        yaml.Should().Contain("ports: !override");
    }

    // ── Service discovery env vars ────────────────────────────────────────────

    [Test]
    public void Yaml_SweAgent_AgentfieldServerPointsToControlPlane()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102));
        yaml.Should().Contain("AGENTFIELD_SERVER: \"http://control-plane:8080\"");
    }

    [Test]
    public void Yaml_SweAgent_CallbackUrlPointsToSelf()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102));
        yaml.Should().Contain("AGENT_CALLBACK_URL: \"http://swe-agent:8003\"");
    }

    [Test]
    public void Yaml_SweFast_AgentfieldServerPointsToControlPlane()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102));
        yaml.Should().Contain("AGENT_CALLBACK_URL: \"http://swe-fast:8004\"");
    }

    // ── Port isolation: different builds don't share ports ────────────────────

    [Test]
    public void DifferentPortBlocks_ProduceDifferentYaml()
    {
        var yaml1 = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102));
        var yaml2 = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8103, 8104, 8105));

        yaml1.Should().NotBe(yaml2);
        yaml1.Should().Contain("8100");
        yaml2.Should().Contain("8103");
        yaml2.Should().NotContain("8100");
    }

    // ── Valid YAML structure ──────────────────────────────────────────────────

    [Test]
    public void Yaml_ContainsAllThreeServices()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102));
        yaml.Should().Contain("control-plane:");
        yaml.Should().Contain("swe-agent:");
        yaml.Should().Contain("swe-fast:");
    }

    [Test]
    public void Yaml_StartsWithServicesKey()
    {
        var yaml = DecodeYaml(SweAfProvisioningService.BuildWriteHubPortOverrideCommand(RepoPath, 8100, 8101, 8102));
        yaml.TrimStart().Should().StartWith("services:");
    }
}
