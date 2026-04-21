using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

/// <summary>
/// Tests for <see cref="SweAfProvisioningService.BuildWriteEnvCommand"/>.
/// These are pure static-method tests — no DB or services needed.
/// </summary>
[TestFixture]
public class SweAfProvisioningServiceTests
{
    private const string _repoPath = "/home/user/swe-af";

    /// <summary>
    /// The .env values are wrapped in single quotes for shell safety:
    ///   printf '%s\n' 'KEY=value' 'KEY2=val2' > path/.env
    /// We check for the quoted form: 'KEY=value'.
    /// </summary>
    private static string Q(string kv) => $"'{kv}'";

    // ── claude_code runtime ────────────────────────────────────────────────

    [Test]
    public void ClaudeCode_WithAnthropicKey()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "claude_code",
            AnthropicApiKey = "sk-ant-test",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null);

        cmd.Should().Contain(Q("ANTHROPIC_API_KEY=sk-ant-test"));
        cmd.Should().NotContain("ANTHROPIC_BASE_URL");
    }

    [Test]
    public void ClaudeCode_WithLlmDeploymentBaseUrl()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "claude_code",
            AnthropicApiKey = null,
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, "http://gpu-host:8080");

        cmd.Should().Contain(Q("ANTHROPIC_BASE_URL=http://gpu-host:8080"));
        cmd.Should().NotContain("ANTHROPIC_API_KEY");
    }

    [Test]
    public void ClaudeCode_WithBoth()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "claude_code",
            AnthropicApiKey = "sk-ant-test",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, "http://gpu-host:8080");

        cmd.Should().Contain(Q("ANTHROPIC_BASE_URL=http://gpu-host:8080"));
        cmd.Should().Contain(Q("ANTHROPIC_API_KEY=sk-ant-test"));
    }

    // ── openrouter runtime ─────────────────────────────────────────────────

    [Test]
    public void OpenRouter_WithKeyAndEndpoint()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "openrouter",
            OpenRouterApiKey = "sk-or-v1-test",
            OpenRouterEndpointUrl = "https://openrouter.ai/api/v1",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null);

        cmd.Should().Contain(Q("ANTHROPIC_API_KEY=sk-or-v1-test"));
        cmd.Should().Contain(Q("ANTHROPIC_BASE_URL=https://openrouter.ai/api/v1"));
        cmd.Should().NotContain("AnthropicApiKey");
    }

    [Test]
    public void OpenRouter_DoesNotInjectAnthropicKey()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "openrouter",
            OpenRouterApiKey = "sk-or-v1-test",
            OpenRouterEndpointUrl = "https://openrouter.ai/api/v1",
            AnthropicApiKey = "sk-ant-ignored",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null);

        cmd.Should().Contain(Q("ANTHROPIC_API_KEY=sk-or-v1-test"));
        cmd.Should().NotContain("sk-ant-ignored");
    }

    [Test]
    public void OpenRouter_WithOnlyKey_NoBaseUrl()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "openrouter",
            OpenRouterApiKey = "sk-or-v1-test",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null);

        cmd.Should().Contain(Q("ANTHROPIC_API_KEY=sk-or-v1-test"));
        cmd.Should().NotContain("ANTHROPIC_BASE_URL");
    }

    [Test]
    public void OpenRouter_WithOnlyEndpoint_NoKey()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "openrouter",
            OpenRouterEndpointUrl = "https://openrouter.ai/api/v1",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null);

        cmd.Should().Contain(Q("ANTHROPIC_BASE_URL=https://openrouter.ai/api/v1"));
        cmd.Should().NotContain("ANTHROPIC_API_KEY");
    }

    // ── open_code runtime ──────────────────────────────────────────────────

    [Test]
    public void OpenCode_OnlyInjectsLlmDeploymentBaseUrl()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "open_code",
            AnthropicApiKey = "sk-ant-test",
            OpenRouterApiKey = "sk-or-v1-test",
            OpenRouterEndpointUrl = "https://openrouter.ai/api/v1",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, "http://vllm-proxy:8080");

        cmd.Should().Contain(Q("ANTHROPIC_BASE_URL=http://vllm-proxy:8080"));
        cmd.Should().NotContain("sk-ant-test");
        cmd.Should().NotContain("sk-or-v1-test");
        cmd.Should().NotContain("openrouter.ai");
    }

    // ── Always-injected fields ─────────────────────────────────────────────

    [Test]
    public void AllRuntimes_InjectAgentFieldConstants()
    {
        var config = new SweAfConfigEntity { Runtime = "claude_code" };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null, 8100);

        cmd.Should().Contain(Q("AGENTFIELD_PORT=8100"));
        cmd.Should().Contain(Q("CLAUDE_CODE_MAX_OUTPUT_TOKENS=32000"));
    }

    [Test]
    public void AllRuntimes_InjectRepoToken()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "openrouter",
            RepositoryApiToken = "ghp_testtoken",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null, 8100);

        cmd.Should().Contain(Q("GH_TOKEN=ghp_testtoken"));
    }

    [Test]
    public void AllRuntimes_InjectControlPlaneOAuth()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "open_code",
            ApiKey = "cp-oauth-token",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null);

        cmd.Should().Contain(Q("CLAUDE_CODE_OAUTH_TOKEN=cp-oauth-token"));
    }

    // ── Blank value handling ───────────────────────────────────────────────

    [Test]
    public void OpenRouter_BlankApiKey_OmitsKey()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "openrouter",
            OpenRouterApiKey = "",
            OpenRouterEndpointUrl = "https://openrouter.ai/api/v1",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null);

        cmd.Should().NotContain("ANTHROPIC_API_KEY");
        cmd.Should().Contain("ANTHROPIC_BASE_URL");
    }

    [Test]
    public void ClaudeCode_BlankApiKey_OmitsKey()
    {
        var config = new SweAfConfigEntity
        {
            Runtime = "claude_code",
            AnthropicApiKey = "",
        };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, _repoPath, null);

        cmd.Should().NotContain("ANTHROPIC_API_KEY");
    }

    [Test]
    public void BlankRepoPath_ProducesValidCommand()
    {
        var config = new SweAfConfigEntity { Runtime = "claude_code" };

        var cmd = SweAfProvisioningService.BuildWriteEnvCommand(config, "/tmp", null);

        cmd.Should().StartWith("printf");
        cmd.Should().EndWith("> /tmp/.env");
    }
}
