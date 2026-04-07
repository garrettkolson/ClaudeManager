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
    private const string DefaultSweAfRepoPath = "~/swe-af";

    /// <summary>
    /// Runs the AgentField control plane using Docker container.
    /// Returns the control plane URL on success, or an error message on failure.
    /// </summary>
    public async Task<(bool Success, string? Error, string? ControlPlaneUrl)>
        ProvisionControlPlaneAsync(CancellationToken ct = default)
    {
        // TODO: convert all this to use the centralized Docker command logic
        var config = await GetConfigAsync(ct);

        if (!IsProvisioningConfigured(config))
        {
            return (false, "Provisioning host is not configured. Please configure SSH credentials and provisioning host first.", null);
        }

        var hostUrl = $"http://{config.ProvisionHost}:8080";

        var repoPath = string.IsNullOrWhiteSpace(config.SweAfRepoPath)
            ? DefaultSweAfRepoPath
            : config.SweAfRepoPath;

        logger.LogInformation(
            "Writing .env and starting SWE-AF stack via docker compose on {Host} (repo: {Path})",
            config.ProvisionHost, repoPath);

        // Step 1: Write .env for docker compose
        var anthropicBaseUrl = await ResolveAnthropicBaseUrlAsync(config, ct);
        var writeEnvCmd = BuildWriteEnvCommand(config, repoPath, anthropicBaseUrl);
        var (envStdout, envStderr, envExitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(writeEnvCmd, ct)
            : await ExecSshShellAsync(config, writeEnvCmd, ct);

        if (envExitCode != 0)
        {
            var errorMsg = (envStderr ?? envStdout ?? "Failed to write .env").Trim();
            logger.LogWarning(".env write failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return (false, $"Failed to write agent .env: {errorMsg}", null);
        }

        // Step 2: Tear down any existing stack, then bring everything back up
        var downCmd = $"cd {repoPath} && docker compose down";
        var (downStdout, downStderr, _) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(downCmd, ct)
            : await ExecSshShellAsync(config, downCmd, ct);
        logger.LogInformation("docker compose down on {Host}: {Out}", config.ProvisionHost,
            (downStdout ?? downStderr ?? "").Trim());

        var composeCmd = $"cd {repoPath} && docker compose up -d --build";
        var (compStdout, compStderr, compExitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(composeCmd, ct)
            : await ExecSshShellAsync(config, composeCmd, ct);

        if (compExitCode != 0)
        {
            var errorMsg = (compStderr ?? compStdout ?? "docker compose failed").Trim();
            logger.LogWarning("docker compose failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return (false, $"docker compose up failed: {errorMsg}", null);
        }

        logger.LogInformation(
            "SWE-AF stack started successfully on {Host} -> {Url}",
            config.ProvisionHost, hostUrl);

        return (true, null, hostUrl);
    }

    /// <summary>
    /// Tears down the AgentField control plane on the configured host.
    /// Returns an error message on failure, or null on success.
    /// </summary>
    public async Task<string?> StopControlPlaneAsync(CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct);

        if (!IsProvisioningConfigured(config))
            return "Provisioning host is not configured.";

        var repoPath = string.IsNullOrWhiteSpace(config.SweAfRepoPath)
            ? DefaultSweAfRepoPath
            : config.SweAfRepoPath;

        logger.LogInformation("Stopping SWE-AF stack via docker compose down on {Host}", config.ProvisionHost);

        var downCmd = $"cd {repoPath} && docker compose down";
        var (stdout, stderr, exitCode) = IsLocalHost(config.ProvisionHost!)
            ? await ExecLocalShellAsync(downCmd, ct)
            : await ExecSshShellAsync(config, downCmd, ct);

        if (exitCode != 0)
        {
            var errorMsg = (stderr ?? stdout ?? "docker compose down failed").Trim();
            logger.LogWarning("docker compose down failed on {Host}: {Error}", config.ProvisionHost, errorMsg);
            return errorMsg;
        }

        logger.LogInformation("SWE-AF stack stopped on {Host}", config.ProvisionHost);
        return null;
    }

    private bool IsProvisioningConfigured(SweAfConfigEntity config) =>
        !string.IsNullOrWhiteSpace(config.ProvisionHost);

    private async Task<string?> ResolveAnthropicBaseUrlAsync(SweAfConfigEntity config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.LlmDeploymentId))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deployment = await db.LlmDeployments
            .FirstOrDefaultAsync(d => d.DeploymentId == config.LlmDeploymentId, ct);
        if (deployment is null)
            return null;

        var host = await db.GpuHosts
            .FirstOrDefaultAsync(h => h.HostId == deployment.HostId, ct);
        if (host is null)
            return null;

        // Prefer the nginx proxy when configured — it round-robins across all running
        // vLLM instances on the host rather than pinning to a single container port.
        if (host.ProxyPort.HasValue)
            return $"http://{host.Host}:{host.ProxyPort}";

        return $"http://{host.Host}:{deployment.HostPort}";
    }

    /// <summary>
    /// Builds a shell command that writes a .env file for docker compose,
    /// using single-quoted heredoc so values are never shell-expanded.
    /// </summary>
    private static string BuildWriteEnvCommand(SweAfConfigEntity config, string repoPath, string? anthropicBaseUrl)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(anthropicBaseUrl))
            lines.Add($"ANTHROPIC_BASE_URL={anthropicBaseUrl}");
        if (!string.IsNullOrWhiteSpace(config.AnthropicApiKey))
            lines.Add($"ANTHROPIC_API_KEY={config.AnthropicApiKey}");
        if (!string.IsNullOrWhiteSpace(config.RepositoryApiToken))
            lines.Add($"GH_TOKEN={config.RepositoryApiToken}");
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            lines.Add($"CLAUDE_CODE_OAUTH_TOKEN={config.ApiKey}");

        var quoted = string.Join(" ", lines.Select(l => "'" + l.Replace("'", "'\\''") + "'"));
        return $"printf '%s\\n' {quoted} > {repoPath}/.env";
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecLocalShellAsync(
        string command, CancellationToken ct)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/C {command}")
                : new ProcessStartInfo("bash", $"-c {ShellQuote(command)}");

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
        catch (OperationCanceledException) { return (null, "Operation cancelled.", 1); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local shell command failed");
            return (null, ex.Message, 1);
        }
    }

    private async Task<(string? Stdout, string? Stderr, int ExitCode)> ExecSshShellAsync(
        SweAfConfigEntity config, string command, CancellationToken ct, bool useSudo = true)
    {
        var auth = BuildAuth(config);
        if (auth is null)
            return (null, "No SSH authentication configured (set SshKeyPath or SshPassword).", 1);

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(
                config.ProvisionHost!, config.SshPort, config.SshUser!, auth);

            using var client = new SshClient(connInfo);
            await Task.Run(() => client.Connect(), ct);

            string? stdout, stderr;
            int exitCode;

            if (useSudo && config.RequiresSudo && !string.IsNullOrWhiteSpace(config.SudoPassword))
            {
                (stdout, stderr, exitCode) = await ExecuteWithSudoAsync(client, command, config.SudoPassword!, ct);
            }
            else
            {
                using var cmd = client.RunCommand(command);
                stdout = cmd.Result;
                stderr = cmd.Error;
                exitCode = cmd.ExitStatus ?? 0;
            }

            client.Disconnect();
            return (stdout, stderr, exitCode);
        }
        catch (OperationCanceledException) { return (null, "Operation cancelled.", 1); }
        catch (Exception ex)
        {
            logger.LogError(ex, "SSH shell command failed on {Host}", config.ProvisionHost);
            return (null, ex.Message, 1);
        }
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

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
