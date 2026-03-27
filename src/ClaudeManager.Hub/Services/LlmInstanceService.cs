using System.Diagnostics;
using System.Text;
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

    public LlmInstanceService(ILogger<LlmInstanceService> logger)
    {
        _logger = logger;
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

        if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            return (null, stderr.Trim());

        return (stdout ?? "", null);
    }

    // ── Command builder ───────────────────────────────────────────────────────

    internal static string BuildDockerRunCommand(LlmDeploymentEntity deployment, string? hfToken)
    {
        var sb = new StringBuilder("docker run -d");

        // GPU assignment
        sb.Append($" --gpus \"device={deployment.GpuIndices}\"");

        // Port mapping (container always listens on 8000)
        sb.Append($" -p {deployment.HostPort}:8000");

        // HuggingFace cache volume
        sb.Append(" -v ~/.cache/huggingface:/root/.cache/huggingface");

        // HuggingFace token (if provided)
        if (!string.IsNullOrWhiteSpace(hfToken))
            sb.Append($" -e HUGGING_FACE_HUB_TOKEN={hfToken}");

        // vLLM image
        var tag = string.IsNullOrWhiteSpace(deployment.ImageTag) ? "latest" : deployment.ImageTag;
        sb.Append($" vllm/vllm-openai:{tag}");

        // Model
        sb.Append($" --model {deployment.ModelId}");

        // Quantization
        if (deployment.Quantization is not "none")
            sb.Append($" --quantization {deployment.Quantization}");

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

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(host.Host, host.SshPort, host.SshUser!, auth);
            using var client = new SshClient(connInfo);

            await Task.Run(() => client.Connect(), ct);
            using var cmd = client.RunCommand(command);
            client.Disconnect();

            return (cmd.Result, cmd.Error, cmd.ExitStatus ?? 0);
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
