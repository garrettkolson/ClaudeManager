using System.Diagnostics;
using System.Text;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Provisions (deploys via Docker) an AgentField control plane on a host.
/// Uses Process.Start for localhost machines and SSH.NET for remote machines.
/// </summary>
public class SweAfProvisioningService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly ILogger<SweAfProvisioningService> _logger;

    public SweAfProvisioningService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        ILogger<SweAfProvisioningService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Deploys the AgentField control plane container on the configured host.
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

        var dockerCommand = BuildDockerCommand(config);
        _logger.LogInformation(
            "Provisioning control plane on {Host} (sudo: {RequiresSudo}): {Command}",
            config.ProvisionHost, config.RequiresSudo, dockerCommand);

        var (stdout, stderr, exitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalAsync(dockerCommand, config, ct)
            : await ExecSshAsync(config, dockerCommand, ct);

        if (exitCode != 0)
        {
            var errorMsg = (stderr ?? stdout ?? "Docker command failed").Trim();
            _logger.LogWarning(
                "Provisioning failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return (false, errorMsg, null);
        }

        // Extract container ID from output (docker run -d returns the ID)
        var containerId = (stdout ?? "").Trim();

        var hostUrl = BuildControlPlaneUrl(config);
        _logger.LogInformation(
            "Control plane provisioned successfully on {Host}: {ContainerId} -> {Url}",
            config.ProvisionHost, containerId[..Math.Min(12, containerId.Length)], hostUrl);

        return (true, null, hostUrl);
    }

    private bool IsProvisioningConfigured(SweAfConfigEntity config) =>
        !string.IsNullOrWhiteSpace(config.ProvisionHost);

    private static string BuildDockerCommand(SweAfConfigEntity config)
    {
        var sb = new StringBuilder("docker run -d --name agentfield-control-plane -p 8080:8080 agentfield/control-plane:latest");
        return config.RequiresSudo ? $"sudo {sb}" : sb.ToString();
    }

    private static string BuildControlPlaneUrl(SweAfConfigEntity config) =>
        $"http://{config.ProvisionHost}:8080";

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecLocalAsync(
        string command, SweAfConfigEntity config, CancellationToken ct)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/C {command}")
                : new ProcessStartInfo("bash", $"-c \"{command.Replace("\"", "\\\"")}\"");

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
            _logger.LogError(ex, "Local docker command failed");
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecSshAsync(
        SweAfConfigEntity config, string command, CancellationToken ct)
    {
        var auth = BuildAuth(config);
        if (auth is null)
        {
            return (null, "No SSH authentication configured (set SshKeyPath or SshPassword).", 1);
        }

        bool requiresSudo = config.RequiresSudo;
        string finalCommand = requiresSudo ? $"sudo {command}" : command;

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
                    client, command, config.SudoPassword!, ct);
            }
            else if (requiresSudo)
            {
                client.Disconnect();
                return (null, "Sudo is required but no sudo password configured.", 1);
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
            _logger.LogError(ex, "SSH provisioning failed on {Host}", config.ProvisionHost);
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecuteWithSudoAsync(
        SshClient client, string command, string sudoPassword, CancellationToken ct)
    {
        try
        {
            var sudoCommand = $"echo '{sudoPassword}' | sudo -S {command}";

            using var cmd = client.RunCommand(sudoCommand);
            await Task.Run(() => { }, ct);

            var stdout = cmd.Result;
            var stderr = cmd.Error;
            var exitCode = cmd.ExitStatus ?? 0;

            return (stdout, stderr, exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute sudo command via SSH");
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
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SweAfConfigs.FirstOrDefaultAsync(ct) ?? new SweAfConfigEntity();
    }
}
