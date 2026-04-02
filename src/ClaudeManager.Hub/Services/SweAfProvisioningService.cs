using System.Diagnostics;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Provisions (runs via Docker) an AgentField control plane on a host.
/// Uses Docker on localhost machines and SSH + Docker for remote machines.
/// </summary>
public class SweAfProvisioningService(
    IDbContextFactory<ClaudeManagerDbContext> dbFactory,
    ILogger<SweAfProvisioningService> logger)
{
    private const string ControlPlaneImage = "agentfield/control-plane:latest";
    private const string ControlPlaneContainerName = "agentfield-control-plane";

    /// <summary>
    /// Runs the AgentField control plane using Docker container.
    /// Returns the control plane URL on success, or an error message on failure.
    /// </summary>
    public async Task<(bool Success, string? Error, string? ControlPlaneUrl)>
        ProvisionControlPlaneAsync(CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);

        if (!IsProvisioningConfigured(config))
        {
            return (false, "Provisioning host is not configured. Please configure SSH credentials and provisioning host first.", null);
        }

        var hostUrl = $"http://{config.ProvisionHost}:8080";
        logger.LogInformation(
            "Starting control plane container on {Host}: image={Image}, container={Container}",
            config.ProvisionHost, ControlPlaneImage, ControlPlaneContainerName);

        // Step 1: Ensure Docker is available and container is stopped/removed
        await StopAndRemoveContainerAsync(config, ct);

        // Step 2: Pull the Docker image
        var (pullStdout, pullStderr, pullExitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalDockerAsync($"pull {ControlPlaneImage}", ct)
            : await ExecSshDockerAsync(config, $"pull {ControlPlaneImage}", ct);

        if (pullExitCode != 0)
        {
            var errorMsg = (pullStderr ?? pullStdout ?? "Pull failed").Trim();
            logger.LogWarning("Pull failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return (false, errorMsg, null);
        }

        // Step 3: Run the container
        var dockerRunArgs = BuildDockerRunArgs(config);
        var (runStdout, runStderr, runExitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalDockerAsync(dockerRunArgs, ct)
            : await ExecSshDockerAsync(config, dockerRunArgs, ct);

        if (runExitCode != 0)
        {
            var errorMsg = (runStderr ?? runStdout ?? "Run failed").Trim();
            logger.LogWarning(
                "Run failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return (false, errorMsg, null);
        }

        logger.LogInformation(
            "Control plane started successfully on {Host} -> {Url}",
            config.ProvisionHost, hostUrl);

        return (true, null, hostUrl);
    }

    private bool IsProvisioningConfigured(SweAfConfigEntity config) =>
        !string.IsNullOrWhiteSpace(config.ProvisionHost);

    private static string BuildDockerRunArgs(SweAfConfigEntity config)
    {
        // Build Docker run command with container name, ports, and restart policy
        // Use nohup equivalent by not attaching to stdout/stderr for remote execution
        return $"run -d --name {ControlPlaneContainerName} " +
               $"--restart unless-stopped " +
               $"-p 8080:8080 " +
               $"{ControlPlaneImage}";
    }

    private async Task StopAndRemoveContainerAsync(SweAfConfigEntity config, CancellationToken ct)
    {
        var stopArgs = $"stop {ControlPlaneContainerName}";
        var rmArgs = $"rm -f {ControlPlaneContainerName}";

        try
        {
            await (IsLocalHost(config.ProvisionHost!)
                ? ExecLocalDockerAsync(stopArgs, ct)
                : ExecSshDockerAsync(config, stopArgs, ct));

            await (IsLocalHost(config.ProvisionHost!)
                ? ExecLocalDockerAsync(rmArgs, ct)
                : ExecSshDockerAsync(config, rmArgs, ct));
        }
        catch
        {
            // Container may not exist, ignore errors
            logger.LogInformation("Container {Container} does not exist, skipping removal", ControlPlaneContainerName);
        }
    }

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecLocalDockerAsync(
        string dockerArgs, CancellationToken ct)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/C docker {dockerArgs}")
                : new ProcessStartInfo("bash", $"-c \"docker {dockerArgs.Replace("\"", "\\\"")}\"");

            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

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
            logger.LogError(ex, "Local Docker command failed");
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecSshDockerAsync(
        SweAfConfigEntity config, string dockerArgs, CancellationToken ct)
    {
        var auth = BuildAuth(config);
        if (auth is null)
        {
            return (null, "No SSH authentication configured (set SshKeyPath or SshPassword).", 1);
        }

        var requiresSudo = config.RequiresSudo;
        var finalCommand = $"docker {dockerArgs}";

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(
                config.ProvisionHost!,
                config.SshPort,
                config.SshUser!,
                auth);

            using var client = new SshClient(connInfo);
            await Task.Run(() => client.Connect(), ct);

            string? stdout;
            string? stderr;
            int exitCode;

            if (requiresSudo && !string.IsNullOrWhiteSpace(config.SudoPassword))
            {
                (stdout, stderr, exitCode) = await ExecuteWithSudoAsync(
                    client, finalCommand, config.SudoPassword!, ct);
            }
            else if (requiresSudo)
            {
                client.Disconnect();
                return (null, "Sudo is required for Docker but no sudo password configured.", 1);
            }
            else
            {
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
            logger.LogError(ex, "SSH Docker command failed on {Host}", config.ProvisionHost);
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecuteWithSudoAsync(
        SshClient client, string command, string sudoPassword, CancellationToken ct)
    {
        try
        {
            var sudoCommand = $"echo '{sudoPassword}' | sudo -S sh -c \"{command}\"";

            using var cmd = client.RunCommand(sudoCommand);
            var stdout = cmd.Result;
            var stderr = cmd.Error;
            var exitCode = cmd.ExitStatus ?? 0;

            return (stdout, stderr, exitCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute sudo command via SSH");
            return (null, ex.Message, 1);
        }
    }

    private AuthenticationMethod? BuildAuth(SweAfConfigEntity config)
    {
        if (!string.IsNullOrWhiteSpace(config.SshKeyPath))
        {
            var path = config.SshKeyPath.Replace("~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new PrivateKeyAuthenticationMethod(
                config.SshUser!,
                new PrivateKeyFile(path));
        }

        if (!string.IsNullOrWhiteSpace(config.SshPassword))
        {
            return new PasswordAuthenticationMethod(
                config.SshUser!,
                config.SshPassword!);
        }

        return null;
    }

    private async Task<SweAfConfigEntity> GetConfigAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SweAfConfigs.FirstOrDefaultAsync(ct) ?? new SweAfConfigEntity();
    }
}
