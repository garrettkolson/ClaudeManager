using System.Diagnostics;
using ClaudeManager.Hub.Models;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Launches the ClaudeManager.Agent process on a configured machine.
/// Uses Process.Start for localhost machines and SSH.NET for remote machines.
/// </summary>
public class AgentLaunchService
{
    private readonly IReadOnlyList<KnownMachineConfig> _configs;
    private readonly ILogger<AgentLaunchService> _logger;

    public AgentLaunchService(
        IReadOnlyList<KnownMachineConfig> configs,
        ILogger<AgentLaunchService> logger)
    {
        _configs = configs;
        _logger  = logger;
    }

    /// <summary>Returns true if a launch configuration exists for this machine.</summary>
    public bool HasLaunchConfig(string machineId) =>
        _configs.Any(c => c.MachineId == machineId);

    /// <summary>
    /// Attempts to start the agent on the specified machine.
    /// Returns (true, null) on success or (false, errorMessage) on failure.
    /// </summary>
    public async Task<(bool Success, string? Error)> LaunchAgentAsync(
        string machineId, CancellationToken ct = default)
    {
        var config = _configs.FirstOrDefault(c => c.MachineId == machineId);
        if (config is null)
            return (false, $"No launch configuration found for machine '{machineId}'.");

        _logger.LogInformation("Launching agent on {MachineId} ({Host})", machineId, config.Host);

        return IsLocalHost(config.Host)
            ? LaunchLocal(config)
            : await LaunchViaSshAsync(config, ct);
    }

    // ── Local launch ──────────────────────────────────────────────────────────

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private (bool, string?) LaunchLocal(KnownMachineConfig config)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe", $"/C {config.AgentCommand}")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo("bash", $"-c \"{config.AgentCommand.Replace("\"", "\\\"")}\"")
                {
                    UseShellExecute = false,
                };
            }

            Process.Start(psi);
            _logger.LogInformation("Local agent launch started for {MachineId}", config.MachineId);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local agent launch failed for {MachineId}", config.MachineId);
            return (false, ex.Message);
        }
    }

    // ── SSH launch ────────────────────────────────────────────────────────────

    private async Task<(bool, string?)> LaunchViaSshAsync(
        KnownMachineConfig config, CancellationToken ct)
    {
        var auth = BuildAuth(config);
        if (auth is null)
            return (false,
                "No SSH authentication configured. Set SshKeyPath or SshPassword in KnownMachines config.");

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(config.Host, config.Port, config.SshUser, auth);
            using var client = new SshClient(connInfo);

            await Task.Run(() => client.Connect(), ct);
            _logger.LogInformation("SSH connected to {Host} for {MachineId}", config.Host, config.MachineId);

            using var cmd = client.RunCommand(config.AgentCommand);
            client.Disconnect();

            // A non-zero exit is only a hard failure if there is stderr output;
            // backgrounded commands (nohup ... &) always return 0 but we're lenient here.
            if (cmd.ExitStatus != 0 && !string.IsNullOrWhiteSpace(cmd.Error))
            {
                var err = cmd.Error.Trim();
                _logger.LogWarning(
                    "Agent launch command exited {Code} on {MachineId}: {Error}",
                    cmd.ExitStatus, config.MachineId, err);
                return (false, $"Remote command failed (exit {cmd.ExitStatus}): {err}");
            }

            _logger.LogInformation("Agent launch command sent to {MachineId}", config.MachineId);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "Launch cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH launch failed for {MachineId}", config.MachineId);
            return (false, ex.Message);
        }
    }

    private static AuthenticationMethod? BuildAuth(KnownMachineConfig config)
    {
        if (config.SshKeyPath is not null)
        {
            var path = config.SshKeyPath.Replace("~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new PrivateKeyAuthenticationMethod(config.SshUser, new PrivateKeyFile(path));
        }

        if (config.SshPassword is not null)
            return new PasswordAuthenticationMethod(config.SshUser, config.SshPassword);

        return null;
    }
}
