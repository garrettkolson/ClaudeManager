using System.Net.Http;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class LlmInstanceServiceTests
{
    private LlmInstanceService _svc = default!;

    [SetUp]
    public void SetUp() =>
        _svc = new LlmInstanceService(NullLogger<LlmInstanceService>.Instance, new HttpClient());

    // ── BuildDockerRunCommand ──────────────────────────────────────────────────

    [Test]
    public void BuildDockerRunCommand_BasicDeployment_ContainsRequiredParts()
    {
        var deployment = MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", gpuIndices: "0", port: 8001);

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().Contain("docker run -d");
        cmd.Should().Contain("--runtime nvidia");
        cmd.Should().Contain("--gpus '\"device=0\"'");
        cmd.Should().Contain("--ipc=host");
        cmd.Should().Contain("-p 8001:8000");
        cmd.Should().Contain("-v ~/.cache/huggingface:/root/.cache/huggingface");
        cmd.Should().Contain("vllm/vllm-openai:latest");
        cmd.Should().Contain("--host 0.0.0.0 --port 8000");
        cmd.Should().Contain("--model meta-llama/Llama-3.1-8B-Instruct");
    }

    [Test]
    public void BuildDockerRunCommand_LatestTag_UsesLatestImage()
    {
        var deployment = MakeDeployment("model/m", gpuIndices: "0", port: 8001);
        deployment.ImageTag = "latest";

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().Contain("vllm/vllm-openai:latest");
    }

    [Test]
    public void BuildDockerRunCommand_NightlyTag_UsesNightlyImage()
    {
        var deployment = MakeDeployment("model/m", gpuIndices: "0", port: 8001);
        deployment.ImageTag = "nightly";

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().Contain("vllm/vllm-openai:nightly");
        cmd.Should().NotContain("vllm/vllm-openai:latest");
    }

    [Test]
    public void BuildDockerRunCommand_EmptyTag_FallsBackToLatest()
    {
        var deployment = MakeDeployment("model/m", gpuIndices: "0", port: 8001);
        deployment.ImageTag = "";

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().Contain("vllm/vllm-openai:latest");
    }

    [Test]
    public void BuildDockerRunCommand_WithHfToken_InjectsEnvVar()
    {
        var deployment = MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", gpuIndices: "0", port: 8001);

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: "hf_abc123");

        cmd.Should().Contain("-e HUGGING_FACE_HUB_TOKEN=hf_abc123");
    }

    [Test]
    public void BuildDockerRunCommand_NullToken_OmitsEnvVar()
    {
        var deployment = MakeDeployment("mistral/Mistral-7B-v0.1", gpuIndices: "0", port: 8002);

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().NotContain("HUGGING_FACE_HUB_TOKEN");
    }

    [Test]
    public void BuildDockerRunCommand_EmptyToken_OmitsEnvVar()
    {
        var deployment = MakeDeployment("mistral/Mistral-7B-v0.1", gpuIndices: "0", port: 8002);

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: "  ");

        cmd.Should().NotContain("HUGGING_FACE_HUB_TOKEN");
    }

    [Test]
    public void BuildDockerRunCommand_MultipleGpus_FormatsDeviceArgAndTensorParallel()
    {
        var deployment = MakeDeployment("meta-llama/Llama-3.1-70B-Instruct", gpuIndices: "0,1,2,3", port: 8001);

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().Contain("--gpus '\"device=0,1,2,3\"'");
        cmd.Should().Contain("--tensor-parallel-size 4");
    }

    [Test]
    public void BuildDockerRunCommand_SingleGpu_OmitsTensorParallel()
    {
        var deployment = MakeDeployment("meta-llama/Llama-3.1-8B-Instruct", gpuIndices: "0", port: 8001);

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().NotContain("--tensor-parallel-size");
    }

    [Test]
    public void BuildDockerRunCommand_AwqQuantization_AddsQuantizationFlag()
    {
        var deployment = MakeDeployment("model/model", gpuIndices: "0", port: 8001, quantization: "awq");

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().Contain("--quantization awq");
    }

    [Test]
    public void BuildDockerRunCommand_NoneQuantization_OmitsQuantizationFlag()
    {
        var deployment = MakeDeployment("model/model", gpuIndices: "0", port: 8001, quantization: "none");

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().NotContain("--quantization");
    }

    [Test]
    public void BuildDockerRunCommand_ExtraArgs_AppendedAtEnd()
    {
        var deployment = MakeDeployment("model/model", gpuIndices: "0", port: 8001);
        deployment.ExtraArgs = "--max-model-len 4096 --dtype float16";

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().EndWith("--max-model-len 4096 --dtype float16");
    }

    [Test]
    public void BuildDockerRunCommand_NullExtraArgs_NoTrailingSpace()
    {
        var deployment = MakeDeployment("model/model", gpuIndices: "0", port: 8001);
        deployment.ExtraArgs = null;

        var cmd = LlmInstanceService.BuildDockerRunCommand(deployment, hfToken: null);

        cmd.Should().NotEndWith(" ");
    }

    // ── ParseDockerInspect ────────────────────────────────────────────────────

    [Test]
    public void ParseDockerInspect_FullVllmInspect_ParsesAllFields()
    {
        const string json = """
            [{
              "Config": {
                "Image": "vllm/vllm-openai:latest",
                "Cmd": ["--host","0.0.0.0","--port","8000","--model","meta-llama/Llama-3.1-8B-Instruct",
                        "--quantization","awq","--max-model-len","32768","--tensor-parallel-size","2"]
              },
              "HostConfig": {
                "PortBindings": { "8000/tcp": [{"HostIp":"0.0.0.0","HostPort":"8081"}] },
                "DeviceRequests": [{"Driver":"nvidia","Count":-1,"DeviceIDs":["0","1"],"Capabilities":[["gpu"]],"Options":{}}]
              }
            }]
            """;

        var result = LlmInstanceService.ParseDockerInspect("abc123def456", json);

        result.Should().NotBeNull();
        result!.ContainerId.Should().Be("abc123def456");
        result.ImageTag.Should().Be("latest");
        result.ModelId.Should().Be("meta-llama/Llama-3.1-8B-Instruct");
        result.HostPort.Should().Be(8081);
        result.GpuIndices.Should().Be("0,1");
        result.Quantization.Should().Be("awq");
        result.MaxModelLen.Should().Be(32768);
    }

    [Test]
    public void ParseDockerInspect_NightlyImage_ParsesTag()
    {
        const string json = """
            [{
              "Config": { "Image": "vllm/vllm-openai:nightly", "Cmd": ["--model","org/model"] },
              "HostConfig": {
                "PortBindings": { "8000/tcp": [{"HostPort":"9000"}] },
                "DeviceRequests": [{"DeviceIDs":["0"]}]
              }
            }]
            """;

        var result = LlmInstanceService.ParseDockerInspect("cid", json);

        result.Should().NotBeNull();
        result!.ImageTag.Should().Be("nightly");
        result.HostPort.Should().Be(9000);
        result.GpuIndices.Should().Be("0");
    }

    [Test]
    public void ParseDockerInspect_NoQuantizationOrMaxLen_ReturnsNulls()
    {
        const string json = """
            [{
              "Config": { "Image": "vllm/vllm-openai:latest", "Cmd": ["--model","org/model"] },
              "HostConfig": {
                "PortBindings": { "8000/tcp": [{"HostPort":"8000"}] },
                "DeviceRequests": []
              }
            }]
            """;

        var result = LlmInstanceService.ParseDockerInspect("cid", json);

        result.Should().NotBeNull();
        result!.Quantization.Should().BeNull();
        result.MaxModelLen.Should().BeNull();
        result.GpuIndices.Should().BeNull();
    }

    [Test]
    public void ParseDockerInspect_NonVllmImage_ReturnsNull()
    {
        const string json = """
            [{"Config":{"Image":"nginx:alpine","Cmd":[]},"HostConfig":{"PortBindings":{},"DeviceRequests":[]}}]
            """;

        LlmInstanceService.ParseDockerInspect("cid", json).Should().BeNull();
    }

    [Test]
    public void ParseDockerInspect_InvalidJson_ReturnsNull()
    {
        LlmInstanceService.ParseDockerInspect("cid", "not-json").Should().BeNull();
    }

    [Test]
    public void ParseDockerInspect_EmptyArray_ReturnsNull()
    {
        LlmInstanceService.ParseDockerInspect("cid", "[]").Should().BeNull();
    }

    // ── DiscoverAsync — remote host, no auth ──────────────────────────────────

    [Test]
    public async Task StartContainerAsync_RemoteHost_NoAuth_ReturnsError()
    {
        var host = new GpuHostEntity
        {
            Host        = "192.168.1.50",
            SshPort     = 22,
            SshUser     = "ubuntu",
            SshKeyPath  = null,
            SshPassword = null,
        };
        var deployment = MakeDeployment("model/model", "0", 8001);

        var (containerId, error) = await _svc.StartContainerAsync(host, deployment, null);

        containerId.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task StopContainerAsync_RemoteHost_NoAuth_ReturnsError()
    {
        var host = new GpuHostEntity
        {
            Host        = "192.168.1.50",
            SshPort     = 22,
            SshUser     = "ubuntu",
            SshKeyPath  = null,
            SshPassword = null,
        };

        var error = await _svc.StopContainerAsync(host, "abc123container");

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task GetLogsAsync_RemoteHost_NoAuth_ReturnsError()
    {
        var host = new GpuHostEntity
        {
            Host        = "192.168.1.50",
            SshPort     = 22,
            SshUser     = "ubuntu",
            SshKeyPath  = null,
            SshPassword = null,
        };

        var (logs, error) = await _svc.GetLogsAsync(host, "abc123container");

        logs.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LlmDeploymentEntity MakeDeployment(
        string modelId, string gpuIndices, int port, string quantization = "none") =>
        new()
        {
            DeploymentId = "test-deploy",
            HostId       = "test-host",
            ModelId      = modelId,
            GpuIndices   = gpuIndices,
            HostPort     = port,
            Quantization = quantization,
        };
}
