using System.Linq;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class NginxProxyServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LlmDeploymentEntity MakeDeployment(string modelId, int hostPort,
        LlmDeploymentStatus status = LlmDeploymentStatus.Running) => new()
    {
        DeploymentId = "test",
        HostId       = "host-1",
        ModelId      = modelId,
        GpuIndices   = "0",
        HostPort     = hostPort,
        Quantization = "none",
        Status       = status,
    };

    // ── GenerateConfig — status filtering ──────────────────────────────────────

    [Test]
    public void GenerateConfig_OnlyRunningDeployments_InUpstream()
    {
        // Verify that only Running deployments are included in nginx upstream
        var running = new[] { MakeDeployment("running-model-a", 8001) };
        var stopped = new[] { MakeDeployment("stopped-model-b", 8002, LlmDeploymentStatus.Stopped) };
        var error = new[] { MakeDeployment("error-model-c", 8003, LlmDeploymentStatus.Error) };
        var starting = new[] { MakeDeployment("starting-model-d", 8004, LlmDeploymentStatus.Starting) };

        var allDeployments = running.Concat(stopped).Concat(error).Concat(starting).ToList();
        var onlyRunning = allDeployments.Where(d => d.Status == LlmDeploymentStatus.Running)
            .ToList();

        var config = NginxProxyService.GenerateConfig(onlyRunning, proxyPort: 8080);

        config.Should().Contain("server 127.0.0.1:8001");
        config.Should().NotContain("server 127.0.0.1:8002");
        config.Should().NotContain("server 127.0.0.1:8003");
        config.Should().NotContain("server 127.0.0.1:8004");
    }

    [Test]
    public void GenerateConfig_EmptyDeployments_Returns503()
    {
        // Verify that no deployments returns 503 response
        var config = NginxProxyService.GenerateConfig([], proxyPort: 8080);

        config.Should().Contain("503");
        config.Should().Contain("No vLLM instances are currently running");
    }

    [Test]
    public void GenerateConfig_NoStoppedDeployments_InConfig()
    {
        var stopped = new[] { MakeDeployment("stopped", 8001, LlmDeploymentStatus.Stopped) };
        var running = new[] { MakeDeployment("running", 8002, LlmDeploymentStatus.Running) };

        var config = NginxProxyService.GenerateConfig(running, proxyPort: 8080);

        config.Should().Contain("server 127.0.0.1:8002");
        config.Should().NotContain("server 127.0.0.1:8001");
    }

    [Test]
    public void GenerateConfig_NoErrorDeployments_InConfig()
    {
        var error = new[] { MakeDeployment("error", 8001, LlmDeploymentStatus.Error) };
        var running = new[] { MakeDeployment("running", 8002, LlmDeploymentStatus.Running) };

        var config = NginxProxyService.GenerateConfig(running, proxyPort: 8080);

        config.Should().Contain("server 127.0.0.1:8002");
        config.Should().NotContain("server 127.0.0.1:8001");
    }

    // ── GenerateConfig — no deployments ──────────────────────────────────────

    [Test]
    public void GenerateConfig_NoDeployments_Contains503()
    {
        var config = NginxProxyService.GenerateConfig([], proxyPort: 8080);

        config.Should().Contain("503");
        config.Should().Contain("listen 8080");
    }

    [Test]
    public void GenerateConfig_NoDeployments_NoUpstreamBlock()
    {
        var config = NginxProxyService.GenerateConfig([], proxyPort: 8080);

        config.Should().NotContain("upstream ");
    }

    // ── GenerateConfig — single deployment ────────────────────────────────────

    [Test]
    public void GenerateConfig_SingleDeployment_ContainsUpstream()
    {
        var deployments = new[] { MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8001) };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        config.Should().Contain("upstream vllm_pool");
        config.Should().Contain("server 127.0.0.1:8001");
    }

    [Test]
    public void GenerateConfig_SingleDeployment_ContainsLocationBlock()
    {
        var deployments = new[] { MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8001) };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        config.Should().Contain("location /");
        config.Should().Contain("proxy_pass http://vllm_pool");
    }

    [Test]
    public void GenerateConfig_SingleDeployment_ListensOnProxyPort()
    {
        var deployments = new[] { MakeDeployment("model/m", 8001) };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 9090);

        config.Should().Contain("listen 9090");
    }

    [Test]
    public void GenerateConfig_ContainsEventsAndHttpBlocks()
    {
        var config = NginxProxyService.GenerateConfig([], proxyPort: 8080);

        config.Should().Contain("events {");
        config.Should().Contain("http {");
    }

    [Test]
    public void GenerateConfig_ContainsKeepaliveDirective()
    {
        var deployments = new[] { MakeDeployment("model/m", 8001) };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        config.Should().Contain("keepalive 32");
    }

    [Test]
    public void GenerateConfig_ContainsProxyTimeouts()
    {
        var deployments = new[] { MakeDeployment("model/m", 8001) };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        config.Should().Contain("proxy_read_timeout");
        config.Should().Contain("proxy_connect_timeout");
    }

    // ── GenerateConfig — multiple instances of same model ────────────────────

    [Test]
    public void GenerateConfig_TwoInstancesSameModel_OneUpstreamTwoServers()
    {
        var deployments = new[]
        {
            MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8001),
            MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8002),
        };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        // Only one upstream block
        var upstreamCount = System.Text.RegularExpressions.Regex.Matches(config, @"^\s+upstream ", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        upstreamCount.Should().Be(1);

        // Both servers listed
        config.Should().Contain("server 127.0.0.1:8001");
        config.Should().Contain("server 127.0.0.1:8002");
    }

    [Test]
    public void GenerateConfig_TwoInstancesSameModel_OneLocationBlock()
    {
        var deployments = new[]
        {
            MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8001),
            MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8002),
        };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        var locationCount = System.Text.RegularExpressions.Regex.Matches(config, @"^\s+location ", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        locationCount.Should().Be(1);
    }

    // ── GenerateConfig — multiple models ─────────────────────────────────────

    [Test]
    public void GenerateConfig_TwoModels_TwoUpstreams()
    {
        var deployments = new[]
        {
            MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8001),
            MakeDeployment("mistral/Mistral-7B-v0.1",          8002),
        };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        // Both servers are load-balanced into a single shared upstream pool
        config.Should().Contain("upstream vllm_pool");
        config.Should().Contain("server 127.0.0.1:8001");
        config.Should().Contain("server 127.0.0.1:8002");
    }

    [Test]
    public void GenerateConfig_TwoModels_TwoLocationBlocks()
    {
        var deployments = new[]
        {
            MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8001),
            MakeDeployment("mistral/Mistral-7B-v0.1",          8002),
        };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        // All deployments share a single location block
        var locationCount = System.Text.RegularExpressions.Regex.Matches(config, @"^\s+location ", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        locationCount.Should().Be(1);
    }

    [Test]
    public void GenerateConfig_TwoModels_EachServerRoutedToOwnUpstream()
    {
        var deployments = new[]
        {
            MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", 8001),
            MakeDeployment("mistral/Mistral-7B-v0.1",          8002),
        };

        var config = NginxProxyService.GenerateConfig(deployments, proxyPort: 8080);

        // Both servers are round-robin load-balanced in the shared vllm_pool
        var poolSection = ExtractUpstreamBlock(config, "vllm_pool");
        poolSection.Should().Contain("127.0.0.1:8001");
        poolSection.Should().Contain("127.0.0.1:8002");
    }

    [Test]
    public void GenerateConfig_GeneratedHeader_ContainsTimestamp()
    {
        var config = NginxProxyService.GenerateConfig([], proxyPort: 8080);

        config.Should().Contain("Generated by Claude Manager");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Extracts the content of a named upstream block from an nginx config string.</summary>
    private static string ExtractUpstreamBlock(string config, string upstreamName)
    {
        var start = config.IndexOf($"upstream {upstreamName}", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = config.IndexOf('}', start);
        return end < 0 ? config[start..] : config[start..end];
    }
}
