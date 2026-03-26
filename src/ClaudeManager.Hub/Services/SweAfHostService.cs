using System.Diagnostics;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Executes configured shell commands on the AgentField host machine.
/// Uses Process.Start for localhost and SSH.NET for remote hosts.
/// </summary>
public class SweAfHostService
{
    private readonly SweAfHostConfig _config;
    private readonly ILogger<SweAfHostService> _logger;

    public bool IsConfigured => _config.IsConfigured;

    public IReadOnlyList<SweAfHostCommand> Commands =>
        _config.Commands ?? (IReadOnlyList<SweAfHostCommand>)[];

    public SweAfHostService(SweAfHostConfig config, ILogger<SweAfHostService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Runs the given shell command on the configured host.</summary>
    public Task<(bool Success, string? Error)> RunAsync(
        string command, string label, CancellationToken ct = default)
    {
        if (!_config.IsConfigured)
            return Task.FromResult<(bool, string?)>((false, "SWE-AF host is not configured."));

        _logger.LogInformation("SWE-AF host [{Label}]: {Host}", label, _config.Host);

        return IsLocalHost(_config.Host)
            ? Task.FromResult(RunLocal(command, label))
            : RunViaSshAsync(command, label, ct);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private (bool, string?) RunLocal(string command, string label)
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

            if (!string.IsNullOrWhiteSpace(_config.AnthropicBaseUrl))
                psi.Environment["ANTHROPIC_BASE_URL"] = _config.AnthropicBaseUrl;
            if (!string.IsNullOrWhiteSpace(_config.AnthropicApiKey))
                psi.Environment["ANTHROPIC_API_KEY"] = _config.AnthropicApiKey;

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            _logger.LogInformation("SWE-AF host [{Label}] completed locally", label);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SWE-AF host [{Label}] failed locally", label);
            return (false, ex.Message);
        }
    }

    private async Task<(bool, string?)> RunViaSshAsync(
        string command, string label, CancellationToken ct)
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
            _logger.LogInformation("SSH connected to {Host} for [{Label}]", _config.Host, label);

            using var cmd = client.RunCommand(InjectEnvVars(command));
            client.Disconnect();

            // Non-zero exit is only a hard failure when stderr is also non-empty.
            // Stop-like commands (pkill, docker compose down) can return non-zero
            // when nothing was running — that is acceptable.
            if (cmd.ExitStatus != 0 && !string.IsNullOrWhiteSpace(cmd.Error))
            {
                var err = cmd.Error.Trim();
                _logger.LogWarning("SWE-AF host [{Label}] exited {Code}: {Error}", label, cmd.ExitStatus, err);
                return (false, $"Command failed (exit {cmd.ExitStatus}): {err}");
            }

            _logger.LogInformation("SWE-AF host [{Label}] succeeded on {Host}", label, _config.Host);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SWE-AF host [{Label}] SSH failed", label);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Prepends configured Anthropic env vars as inline shell assignments.
    /// Works for direct-process invocations; for Docker Compose, set vars in .env instead.
    /// </summary>
    private string InjectEnvVars(string command)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_config.AnthropicBaseUrl))
            parts.Add($"ANTHROPIC_BASE_URL={QuoteForShell(_config.AnthropicBaseUrl)}");
        if (!string.IsNullOrWhiteSpace(_config.AnthropicApiKey))
            parts.Add($"ANTHROPIC_API_KEY={QuoteForShell(_config.AnthropicApiKey)}");

        return parts.Count == 0 ? command : string.Join(" ", parts) + " " + command;
    }

    private static string QuoteForShell(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

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
