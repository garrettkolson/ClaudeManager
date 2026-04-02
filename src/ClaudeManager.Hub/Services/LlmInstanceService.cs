using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services.Docker;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Executes Docker commands (run / stop / logs) on a GPU host.
/// Uses Process.Start for localhost machines and SSH.NET for remote hosts.
/// </summary>
public class LlmInstanceService(
    ILogger<LlmInstanceService> logger,
    HttpClient httpClient,
    IDockerExecutor dockerExecutor)
{
    //private readonly IDockerExecutor _dockerExecutor = dockerExecutor ?? new DockerExecutor(null);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the vLLM container in detached mode and returns the container ID,
    /// or an error string on failure.
    /// </summary>
    public async Task<(string? ContainerId, string? Error)> StartContainerAsync(
        GpuHostEntity host, LlmDeploymentEntity deployment, string? hfToken,
        CancellationToken ct = default)
    {
        var command = BuildDockerRunCommand(deployment, hfToken);
        logger.LogInformation("Starting vLLM container on {Host}: {Command}", host.Host, command);

        var result = await dockerExecutor.ExecuteAsync(
            command,
            host.ToExecutionHost(),
            ct);

        if (!result.IsSuccess || result.Stdout is null)
        {
            logger.LogWarning("docker run failed on {Host}: {Error}", host.Host, result.Stderr);
            return (null, result.Stderr);
        }

        var containerId = result.Stdout.Trim();
        logger.LogInformation("Container started on {Host}: {ContainerId}", host.Host, containerId[..Math.Min(12, containerId.Length)]);
        return (containerId, null);
    }

    /// <summary>
    /// Forcefully removes the container (docker rm -f), effectively stopping it.
    /// Returns null on success or an error string on failure.
    /// </summary>
    public async Task<string?> StopContainerAsync(
        GpuHostEntity host, string containerId, CancellationToken ct = default)
    {
        var command = $"docker rm -f {containerId}";
        logger.LogInformation("Stopping container {ContainerId} on {Host}", containerId[..Math.Min(12, containerId.Length)], host.Host);

        var result = await dockerExecutor.ExecuteAsync(
            DockerCommand.FromString(command),
            host.ToExecutionHost(),
            ct);

        if (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.Stderr))
            return result.Stderr.Trim();

        return null;
    }

    /// <summary>
    /// Fetches the last <paramref name="lines"/> lines from the container's stdout/stderr.
    /// </summary>
    public async Task<(string? Logs, string? Error)> GetLogsAsync(
        GpuHostEntity host, string containerId, int lines = 200, CancellationToken ct = default)
    {
        var command = $"docker logs --tail {lines} --timestamps {containerId} 2>&1";

        var result = await dockerExecutor.ExecuteAsync(
            DockerCommand.FromString(command),
            host.ToExecutionHost(),
            ct);

        if (!result.IsSuccess)
            return (null, (result.Stderr ?? result.Stdout ?? "docker logs failed").Trim());

        var logs = string.IsNullOrWhiteSpace(result.Stdout) ? (result.Stderr ?? "") : result.Stdout;
        return (logs, null);
    }

    /// <summary>
    /// Checks the health of a vLLM instance by making an HTTP GET request to /health.
    /// Returns true if the instance responds with HTTP 200, false otherwise.
    /// </summary>
    public async Task<bool> CheckHealthAsync(
        GpuHostEntity host, int hostPort, CancellationToken ct = default)
    {
        var url = $"http://{host.Host}:{hostPort}/health";

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(request, timeoutCts.Token);
            return response.StatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Health check timed out for {Url}", url);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Health check failed for {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Inspects a Docker container and returns its status.
    /// Returns null if the container is not found.
    /// </summary>
    public async Task<ContainerStatus?> InspectContainerAsync(
        GpuHostEntity host, string containerId, CancellationToken ct = default)
    {
        var command = $"docker inspect -f '{{{{.State.Status}}}}' {containerId} 2>/dev/null || echo not_found";

        var result = await dockerExecutor.ExecuteAsync(
            DockerCommand.FromString(command),
            host.ToExecutionHost(),
            ct);

        if (result.ExitCode != 0 && result.Stdout?.Trim() == "not_found")
            return null; // Container not found

        return ParseContainerStatus(result.Stdout?.Trim());
    }

    private static ContainerStatus? ParseContainerStatus(string? raw)
    {
        return raw switch
        {
            "running" => ContainerStatus.Running,
            "exited"  => ContainerStatus.Exited,
            "dead"    => ContainerStatus.Dead,
            _         => null,
        };
    }

    // ── Container detection ───────────────────────────────────────────────────

    /// <summary>
    /// Lists all running vLLM (<c>vllm/vllm-openai:*</c>) containers on the given host
    /// by querying <c>docker ps</c> then <c>docker inspect</c> for each container ID found.
    /// </summary>
    public async Task<(List<RawVllmContainerInfo> Containers, string? Error)>
        ListRunningVllmContainersAsync(GpuHostEntity host, CancellationToken ct = default)
    {
        var psResult = await dockerExecutor.ExecuteAsync(
            DockerCommand.FromString("docker ps -q --no-trunc"),
            host.ToExecutionHost(),
            ct);

        if (psResult.ExitCode != 0)
            return ([], $"docker ps failed: {(psResult.Stderr ?? psResult.Stdout ?? "no output").Trim()}");

        var ids = (psResult.Stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

        if (ids.Count == 0) return ([], null);

        var results = new List<RawVllmContainerInfo>();
        foreach (var id in ids)
        {
            var inspectResult = await dockerExecutor.ExecuteAsync(
                DockerCommand.FromString($"docker inspect {id}"),
                host.ToExecutionHost(),
                ct);

            if (inspectResult.ExitCode != 0 || string.IsNullOrWhiteSpace(inspectResult.Stdout)) continue;

            var raw = ParseDockerInspect(id, inspectResult.Stdout);
            if (raw is not null) results.Add(raw);
        }

        return (results, null);
    }

    /// <summary>
    /// Parses a single <c>docker inspect</c> JSON response.
    /// Returns null when the container is not a vLLM instance or the JSON cannot be parsed.
    /// </summary>
    internal static RawVllmContainerInfo? ParseDockerInspect(string containerId, string json)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<DockerInspectResult[]>(json, opts);
            var item = items?.FirstOrDefault();
            if (item is null) return null;

            var image = item.Config?.Image ?? "";
            if (!image.Contains("vllm/vllm-openai", StringComparison.OrdinalIgnoreCase))
                return null;

            var colon = image.LastIndexOf(':');
            var imageTag = colon >= 0 ? image[(colon + 1)..] : "latest";

            var cmd = item.Config?.Cmd;
            var modelId = ParseCmdArg(cmd, "--model");
            var quantization = ParseCmdArg(cmd, "--quantization");
            var maxLenStr = ParseCmdArg(cmd, "--max-model-len");
            int? maxModelLen = maxLenStr is not null && int.TryParse(maxLenStr, out var ml) ? ml : null;

            int? hostPort = null;
            if (item.HostConfig?.PortBindings?.TryGetValue("8000/tcp", out var bindings) == true)
            {
                var first = bindings?.FirstOrDefault();
                if (first?.HostPort is not null && int.TryParse(first.HostPort, out var p))
                    hostPort = p;
            }

            var devReq = item.HostConfig?.DeviceRequests?.FirstOrDefault();
            var gpuIndices = devReq?.DeviceIDs is { Length: > 0 }
                ? string.Join(",", devReq.DeviceIDs)
                : null;

            return new RawVllmContainerInfo(
                ContainerId: containerId,
                ImageTag: imageTag,
                ModelId: modelId,
                HostPort: hostPort,
                GpuIndices: gpuIndices,
                Quantization: quantization,
                MaxModelLen: maxModelLen);
        }
        catch { return null; }
    }

    private static string? ParseCmdArg(string[]? args, string flag)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    // ── Docker inspect JSON models ────────────────────────────────────────────

    private record DockerInspectResult(
        [property: JsonPropertyName("Config")]     DockerInspectConfig?     Config,
        [property: JsonPropertyName("HostConfig")] DockerInspectHostConfig? HostConfig);

    private record DockerInspectConfig(
        [property: JsonPropertyName("Image")] string?   Image,
        [property: JsonPropertyName("Cmd")]   string[]? Cmd);

    private record DockerInspectHostConfig(
        [property: JsonPropertyName("PortBindings")]
        Dictionary<string, DockerPortBinding[]?>? PortBindings,
        [property: JsonPropertyName("DeviceRequests")]
        DockerDeviceRequest[]? DeviceRequests);

    private record DockerPortBinding(
        [property: JsonPropertyName("HostPort")] string? HostPort);

    private record DockerDeviceRequest(
        [property: JsonPropertyName("DeviceIDs")] string[]? DeviceIDs);

    // ── Command builder ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a Docker run command for a vLLM deployment using the builder pattern.
    /// </summary>
    internal DockerCommand BuildDockerRunCommand(LlmDeploymentEntity deployment, string? hfToken)
    {
        var builder = new LlmDeploymentCommandBuilder()
            .WithContainerName($"vllm-{deployment.ModelId.Replace("/", "-").Replace(".", "-")}-{{guid}}")
            .WithImageTag(deployment.ImageTag ?? "latest")
            .WithGpus(deployment.GpuIndices ?? "0")
            .WithNvidiaRuntime()
            .WithHostIPC()
            .WithHostPort(deployment.HostPort)
            .WithHfCacheVolume();

        if (!string.IsNullOrWhiteSpace(hfToken))
            builder = builder.WithHfToken(hfToken);

        if (deployment.Quantization is not "none" && !string.IsNullOrWhiteSpace(deployment.Quantization))
        {
            builder = builder.WithModel(deployment.ModelId, deployment.Quantization, deployment.MaxModelLen);
        }
        else
        {
            builder = builder.WithModel(deployment.ModelId, maxModelLen: deployment.MaxModelLen);
        }

        var gpuCount = deployment.GpuIndices?.Split(',').Length ?? 1;
        if (gpuCount > 1)
            builder = builder.WithTensorParallelSize(gpuCount);

        if (!string.IsNullOrWhiteSpace(deployment.ExtraArgs))
            builder = builder.WithExtraArgs(deployment.ExtraArgs.Trim());

        return builder.Build();
    }
}
