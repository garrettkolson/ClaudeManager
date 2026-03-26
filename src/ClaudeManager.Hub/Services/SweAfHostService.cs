using System.Diagnostics;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Controls the AgentField service process on its host machine.
/// Uses Process.Start for localhost and SSH.NET for remote hosts.
/// </summary>
public class SweAfHostService
{
    private readonly SweAfHostConfig _config;
    private readonly ILogger<SweAfHostService> _logger;

    public bool IsConfigured => _config.IsConfigured;

    public SweAfHostService(SweAfHostConfig config, ILogger<SweAfHostService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task<(bool Success, string? Error)> StartAsync(CancellationToken ct = default)
        => ExecuteCommandAsync(_config.StartCommand, "start", ct);

    public Task<(bool Success, string? Error)> StopAsync(CancellationToken ct = default)
        => ExecuteCommandAsync(_config.StopCommand, "stop", ct);

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task<(bool, string?)> ExecuteCommandAsync(
        string command, string op, CancellationToken ct)
    {
        if (!_config.IsConfigured)
            return (false, "SWE-AF host is not configured.");

        _logger.LogInformation("SWE-AF host {Op}: {Host}", op, _config.Host);

        return IsLocalHost(_config.Host)
            ? RunLocal(command, op)
            : await RunViaSshAsync(command, op, ct);
    }

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private (bool, string?) RunLocal(string command, string op)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe", $"/C {command}")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo("bash", $"-c \"{command.Replace("\"", "\\\"")}\"")
                {
                    UseShellExecute = false,
                };
            }

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            _logger.LogInformation("SWE-AF host {Op} completed locally", op);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SWE-AF host {Op} failed locally", op);
            return (false, ex.Message);
        }
    }

    private async Task<(bool, string?)> RunViaSshAsync(
        string command, string op, CancellationToken ct)
    {
        var auth = BuildAuth();
        if (auth is null)
            return (false,
                "No SSH authentication configured (set SshKeyPath or SshPassword in SweAfHost config).");

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(_config.Host, _config.Port, _config.SshUser, auth);
            using var client = new SshClient(connInfo);

            await Task.Run(() => client.Connect(), ct);
            _logger.LogInformation("SSH connected to {Host} for SWE-AF host {Op}", _config.Host, op);

            using var cmd = client.RunCommand(command);
            client.Disconnect();

            // Non-zero exit is only a hard failure when stderr is also non-empty.
            // Stop commands (pkill, systemctl stop) return non-zero when the service
            // is already stopped — that is acceptable.
            if (cmd.ExitStatus != 0 && !string.IsNullOrWhiteSpace(cmd.Error))
            {
                var err = cmd.Error.Trim();
                _logger.LogWarning("SWE-AF host {Op} exited {Code}: {Error}", op, cmd.ExitStatus, err);
                return (false, $"Command failed (exit {cmd.ExitStatus}): {err}");
            }

            _logger.LogInformation("SWE-AF host {Op} succeeded on {Host}", op, _config.Host);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SWE-AF host {Op} SSH failed", op);
            return (false, ex.Message);
        }
    }

    private AuthenticationMethod? BuildAuth()
    {
        if (_config.SshKeyPath is not null)
        {
            var path = _config.SshKeyPath.Replace("~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new PrivateKeyAuthenticationMethod(_config.SshUser, new PrivateKeyFile(path));
        }

        if (_config.SshPassword is not null)
            return new PasswordAuthenticationMethod(_config.SshUser, _config.SshPassword);

        return null;
    }
}
