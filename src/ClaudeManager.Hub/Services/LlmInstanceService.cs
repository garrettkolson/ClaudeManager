using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeManager.Hub.Persistence.Entities;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Executes Docker commands (run / stop / logs) on a GPU host.
/// Uses Process.Start for localhost machines and SSH.NET for remote hosts.
/// </summary>
public class LlmInstanceService
{
    private readonly ILogger<LlmInstanceService> _logger;
    private readonly HttpClient _httpClient;

    public LlmInstanceService(ILogger<LlmInstanceService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the vLLM container in detached mode and returns the container ID,
    /// or an error string on failure.
    /// </summary>
    public virtual async Task<(string? ContainerId, string? Error)> StartContainerAsync(
        GpuHostEntity host, LlmDeploymentEntity deployment, string? hfToken,
        CancellationToken ct = default)
    {
        var command = BuildDockerRunCommand(deployment, hfToken);
        _logger.LogInformation("Starting vLLM container on {Host}: {Command}", host.Host, command);

        var (stdout, stderr, exitCode) = IsLocalHost(host.Host)
            ? await ExecLocalAsync(command, ct)
            : await ExecSshAsync(host, command, ct);

        if (exitCode != 0 || stdout is null)
        {
            var err = stderr?.Trim() ?? "No output from docker run.";
            _logger.LogWarning("docker run failed on {Host}: {Error}", host.Host, err);
            return (null, err);
        }

        var containerId = stdout.Trim();
        _logger.LogInformation("Container started on {Host}: {ContainerId}", host.Host, containerId[..Math.Min(12, containerId.Length)]);
        return (containerId, null);
    }

    /// <summary>
    /// Forcefully removes the container (docker rm -f), effectively stopping it.
    /// Returns null on success or an error string on failure.
    /// </summary>
    public virtual async Task<string?> StopContainerAsync(
        GpuHostEntity host, string containerId, CancellationToken ct = default)
    {
        var command = $"docker rm -f {containerId}";
        _logger.LogInformation("Stopping container {ContainerId} on {Host}", containerId[..Math.Min(12, containerId.Length)], host.Host);

        var (_, stderr, exitCode) = IsLocalHost(host.Host)
            ? await ExecLocalAsync(command, ct)
            : await ExecSshAsync(host, command, ct);

        if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            return stderr.Trim();

        return null;
    }

    /// <summary>
    /// Fetches the last <paramref name="lines"/> lines from the container's stdout/stderr.
    /// </summary>
    public virtual async Task<(string? Logs, string? Error)> GetLogsAsync(
        GpuHostEntity host, string containerId, int lines = 200, CancellationToken ct = default)
    {
        var command = $"docker logs --tail {lines} --timestamps {containerId} 2>&1";

        var (stdout, stderr, exitCode) = IsLocalHost(host.Host)
            ? await ExecLocalAsync(command, ct)
            : await ExecSshAsync(host, command, ct);

        if (exitCode != 0)
            return (null, (stderr ?? stdout ?? "docker logs failed").Trim());

        // docker routes container stdout → its stdout and container stderr → its stderr.
        // With 2>&1 both should be merged into stdout, but fall back to stderr if stdout is
        // empty (e.g. apps like vLLM that write exclusively to stderr).
        var logs = string.IsNullOrWhiteSpace(stdout) ? (stderr ?? "") : stdout;
        return (logs, null);
    }

    /// <summary>
    /// Checks the health of a vLLM instance by making an HTTP GET request to /health.
    /// Returns true if the instance responds with HTTP 200, false otherwise.
    /// </summary>
    public virtual async Task<bool> CheckHealthAsync(
        GpuHostEntity host, int hostPort, CancellationToken ct = default)
    {
        var url = $"http://{host.Host}:{hostPort}/health";

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            return response.StatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Health check timed out for {Url}", url);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Health check failed for {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Inspects a Docker container and returns its status.
    /// Returns null if the container is not found.
    /// </summary>
    public virtual async Task<ContainerStatus?> InspectContainerAsync(
        GpuHostEntity host, string containerId, CancellationToken ct = default)
    {
        var command = $"docker inspect -f '{{{{.State.Status}}}}' {containerId} 2>/dev/null || echo not_found";

        var (stdout, _, exitCode) = IsLocalHost(host.Host)
            ? await ExecLocalAsync(command, ct)
            : await ExecSshAsync(host, command, ct);

        if (exitCode != 0 && stdout?.Trim() == "not_found")
            return null; // Container not found

        return ParseContainerStatus(stdout?.Trim());
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
    public virtual async Task<(List<RawVllmContainerInfo> Containers, string? Error)>
        ListRunningVllmContainersAsync(GpuHostEntity host, CancellationToken ct = default)
    {
        // 1. Get IDs of all running containers
        const string psCmd = "docker ps -q --no-trunc";
        var (psOut, psErr, psExit) = IsLocalHost(host.Host)
            ? await ExecLocalAsync(psCmd, ct)
            : await ExecSshAsync(host, psCmd, ct);

        if (psExit != 0)
            return ([], $"docker ps failed: {(psErr ?? psOut ?? "no output").Trim()}");

        var ids = (psOut ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

        if (ids.Count == 0) return ([], null);

        // 2. Inspect each container; ParseDockerInspect filters to vLLM images
        var results = new List<RawVllmContainerInfo>();
        foreach (var id in ids)
        {
            var (inspectOut, _, inspectExit) = IsLocalHost(host.Host)
                ? await ExecLocalAsync($"docker inspect {id}", ct)
                : await ExecSshAsync(host, $"docker inspect {id}", ct);

            if (inspectExit != 0 || string.IsNullOrWhiteSpace(inspectOut)) continue;

            var raw = ParseDockerInspect(id, inspectOut);
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
            var opts  = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<DockerInspectResult[]>(json, opts);
            var item  = items?.FirstOrDefault();
            if (item is null) return null;

            var image = item.Config?.Image ?? "";
            if (!image.Contains("vllm/vllm-openai", StringComparison.OrdinalIgnoreCase))
                return null;

            var colon    = image.LastIndexOf(':');
            var imageTag = colon >= 0 ? image[(colon + 1)..] : "latest";

            var cmd          = item.Config?.Cmd;
            var modelId      = ParseCmdArg(cmd, "--model");
            var quantization = ParseCmdArg(cmd, "--quantization");
            var maxLenStr    = ParseCmdArg(cmd, "--max-model-len");
            int? maxModelLen = maxLenStr is not null && int.TryParse(maxLenStr, out var ml) ? ml : null;

            int? hostPort = null;
            if (item.HostConfig?.PortBindings?.TryGetValue("8000/tcp", out var bindings) == true)
            {
                var first = bindings?.FirstOrDefault();
                if (first?.HostPort is not null && int.TryParse(first.HostPort, out var p))
                    hostPort = p;
            }

            var devReq     = item.HostConfig?.DeviceRequests?.FirstOrDefault();
            var gpuIndices = devReq?.DeviceIDs is { Length: > 0 }
                ? string.Join(",", devReq.DeviceIDs)
                : null;

            return new RawVllmContainerInfo(
                ContainerId:  containerId,
                ImageTag:     imageTag,
                ModelId:      modelId,
                HostPort:     hostPort,
                GpuIndices:   gpuIndices,
                Quantization: quantization,
                MaxModelLen:  maxModelLen);
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

    internal static string BuildDockerRunCommand(LlmDeploymentEntity deployment, string? hfToken)
    {
        var sb = new StringBuilder("docker run -d");

        // Runtime and GPU assignment.
        // '"device=X"' passes a JSON-style value to Docker that sets only DeviceIDs (no Count),
        // avoiding the "cannot set both Count and DeviceIDs" error from the NVIDIA container runtime.
        sb.Append(" --runtime nvidia");
        sb.Append($" --gpus '\"device={deployment.GpuIndices}\"'");

        // Port mapping (container always listens on 8000)
        sb.Append($" -p {deployment.HostPort}:8000");

        // HuggingFace cache volume
        sb.Append(" -v ~/.cache/huggingface:/root/.cache/huggingface");

        // HuggingFace token (if provided)
        if (!string.IsNullOrWhiteSpace(hfToken))
            sb.Append($" -e HUGGING_FACE_HUB_TOKEN={hfToken}");

        // Shared memory — required for multi-GPU tensor parallelism
        sb.Append(" --ipc=host");

        // vLLM image
        var tag = string.IsNullOrWhiteSpace(deployment.ImageTag) ? "latest" : deployment.ImageTag;
        sb.Append($" vllm/vllm-openai:{tag}");

        // vLLM bind address and port
        sb.Append(" --host 0.0.0.0 --port 8000");

        // Model
        sb.Append($" --model {deployment.ModelId}");

        // Tensor parallelism — must match the number of GPUs assigned
        var gpuCount = deployment.GpuIndices.Split(',').Length;
        if (gpuCount > 1)
            sb.Append($" --tensor-parallel-size {gpuCount}");

        // Quantization
        if (deployment.Quantization is not "none")
            sb.Append($" --quantization {deployment.Quantization}");

        // Pre-computed max context length
        if (deployment.MaxModelLen.HasValue)
            sb.Append($" --max-model-len {deployment.MaxModelLen.Value}");

        // Extra user-supplied args
        if (!string.IsNullOrWhiteSpace(deployment.ExtraArgs))
            sb.Append($" {deployment.ExtraArgs.Trim()}");

        return sb.ToString();
    }

    // ── Execution helpers ─────────────────────────────────────────────────────

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecLocalAsync(
        string command, CancellationToken ct)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/C {command}")
                : new ProcessStartInfo("bash", $"-c \"{command.Replace("\"", "\\\"")}\"");

            psi.UseShellExecute        = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            psi.CreateNoWindow         = true;

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return (stdout, stderr, proc.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return (null, "Operation cancelled.", 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local docker command failed");
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecSshAsync(
        GpuHostEntity host, string command, CancellationToken ct)
    {
        var auth = BuildAuth(host);
        if (auth is null)
            return (null, "No SSH authentication configured (SshKeyPath or SshPassword required).", 1);

        // Determine if sudo is required and build the command
        bool requiresSudo = host.RequiresSudo;
        string finalCommand = requiresSudo ? $"sudo {command}" : command;

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(host.Host, host.SshPort, host.SshUser!, auth);
            using var client = new SshClient(connInfo);

            await Task.Run(() => client.Connect(), ct);

            string? stdout;
            string? stderr;
            int exitCode;

            if (requiresSudo && !string.IsNullOrWhiteSpace(host.SudoPassword))
            {
                // Use sudo with password via SSH
                (stdout, stderr, exitCode) = await ExecuteWithSudoAsync(client, command, host.SudoPassword!, ct);
            }
            else if (requiresSudo)
            {
                // Sudo required but no password configured
                return (null, "Sudo is required but no sudo password configured for host " + host.Host, 1);
            }
            else
            {
                // No sudo needed
                using var cmd = client.RunCommand(finalCommand);
                stdout = cmd.Result;
                stderr = cmd.Error;
                exitCode = cmd.ExitStatus ?? 0;
            }

            client.Disconnect();
            return (stdout, stderr, exitCode);
        }
        catch (OperationCanceledException)
        {
            return (null, "Operation cancelled.", 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH docker command failed on {Host}", host.Host);
            return (null, ex.Message, 1);
        }
    }

    /// <summary>
    /// Executes a command with sudo privileges using SSH, prompting for password if needed.
    /// </summary>
    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecuteWithSudoAsync(
        SshClient client, string command, string sudoPassword, CancellationToken ct)
    {
        _logger.LogInformation("Executing with sudo on remote host: {Command}", command);

        try
        {
            // Use sudo with password by piping it to sudo -S
            // The command format is: echo "password" | sudo -S <command>
            var sudoCommand = $"echo '{sudoPassword}' | sudo -S {command}";

            using var cmd = client.RunCommand(sudoCommand);
            await Task.Run(() => { }, ct); // Allow cancellation check

            var stdout = cmd.Result;
            var stderr = cmd.Error;
            var exitCode = cmd.ExitStatus ?? 0;

            if (exitCode != 0)
            {
                _logger.LogWarning("Sudo command failed with exit code {ExitCode}: {Stderr}", exitCode, stderr);
            }

            return (stdout, stderr, exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute sudo command: {Command}", command);
            return (null, ex.Message, 1);
        }
    }

    private static AuthenticationMethod? BuildAuth(GpuHostEntity host)
    {
        if (host.SshKeyPath is not null)
        {
            var path = host.SshKeyPath.Replace("~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new PrivateKeyAuthenticationMethod(host.SshUser, new PrivateKeyFile(path));
        }

        if (host.SshPassword is not null)
            return new PasswordAuthenticationMethod(host.SshUser, host.SshPassword);

        return null;
    }
}
