using System.Diagnostics;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services.Docker;

/// <summary>
/// Execution result from running a Docker command.
/// </summary>
public record DockerExecutionResult(
    string? Stdout,
    string? Stderr,
    int ExitCode,
    bool IsSuccess = true
);

/// <summary>
/// Execution result wrapper for compatibility with existing return patterns.
/// </summary>
public record ExecResult(
    string? Stdout,
    string? Stderr,
    int ExitCode
);

/// <summary>
/// Abstraction over Docker command execution on local or remote hosts.
/// Eliminates duplication across services by centralizing execution logic.
/// </summary>
public interface IDockerExecutor
{
    /// <summary>
    /// Executes a Docker command locally or via SSH.
    /// </summary>
    Task<DockerExecutionResult> ExecuteAsync(DockerCommand command, ExecutionHost host, CancellationToken ct);

    /// <summary>
    /// Executes a Docker command locally.
    /// </summary>
    Task<DockerExecutionResult> ExecuteLocalAsync(DockerCommand command, CancellationToken ct);

    /// <summary>
    /// Executes a Docker command via SSH on a remote host.
    /// </summary>
    Task<DockerExecutionResult> ExecuteRemoteAsync(DockerCommand command, string remoteHost, ExecutionHost host, CancellationToken ct);
}

/// <summary>
/// Configuration for remote execution hosts.
/// </summary>
public record ExecutionHost(
    string Host,
    int Port,
    string User,
    HostAuthType AuthType,
    string? SshKeyPath = null,
    string? SshPassword = null,
    string? SudoPassword = null
);

/// <summary>
/// Specifies the authentication method for remote execution.
/// </summary>
public enum HostAuthType
{
    None,
    PrivateKey,
    Password
}

/// <summary>
/// Concrete implementation of DockerExecutor with centralized execution logic.
/// </summary>
public class DockerExecutor : IDockerExecutor
{
    private readonly ILogger? _logger;

    public DockerExecutor(ILogger? logger = null)
    {
        _logger = logger;
    }

    public DockerExecutor(ILogger<DockerExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<DockerExecutionResult> ExecuteAsync(DockerCommand command, ExecutionHost host, CancellationToken ct)
    {
        var isLocal = IsLocalHost(host.Host);

        return isLocal 
            ? await ExecuteLocalAsync(command, ct) 
            : await ExecuteRemoteAsync(command, host.Host, host, ct);
    }

    public async Task<DockerExecutionResult> ExecuteLocalAsync(DockerCommand command, CancellationToken ct)
    {
        return await ExecuteLocalInternalAsync(command.Args, ct);
    }

    private async Task<DockerExecutionResult> ExecuteLocalInternalAsync(string dockerArgs, CancellationToken ct)
    {
        try
        {
            var commandText = OperatingSystem.IsWindows()
                ? $"docker {dockerArgs}"
                : $"docker {dockerArgs}";

            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/C {commandText}")
                : new ProcessStartInfo("bash", $"-c \"{EscapeBashCommand(commandText)}\"");

            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return new DockerExecutionResult(stdout, stderr, proc.ExitCode, proc.ExitCode == 0);
        }
        catch (OperationCanceledException)
        {
            return new DockerExecutionResult(null, "Operation cancelled.", 1, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local Docker command failed: {Command}", dockerArgs);
            return new DockerExecutionResult(null, ex.Message, 1, false);
        }
    }

    public async Task<DockerExecutionResult> ExecuteRemoteAsync(DockerCommand command, string remoteHost, ExecutionHost host, CancellationToken ct)
    {
        var authMethod = BuildAuthMethod(host);
        if (authMethod is null)
        {
            return new DockerExecutionResult(
                null,
                "No SSH authentication configured. Provide SshKeyPath or SshPassword.",
                1,
                false
            );
        }

        var finalCommand = $"docker {command.Args}";

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(
                host.Host,
                host.Port,
                host.User,
                authMethod
            );

            using var client = new SshClient(connInfo);
            await Task.Run(() => client.Connect(), ct);

            string stdout, stderr;
            int exitCode;

            var sudoPassword = !string.IsNullOrEmpty(command.SudoPassword)
                ? command.SudoPassword
                : host.SudoPassword;

            if (command.RequiresSudo && !string.IsNullOrEmpty(sudoPassword))
            {
                (stdout, stderr, exitCode) = await ExecuteWithSudoAsync(client, finalCommand, sudoPassword!, ct);
            }
            else if (command.RequiresSudo)
            {
                client.Disconnect();
                return new DockerExecutionResult(
                    null,
                    $"Sudo is required on {remoteHost} but no sudo password configured.",
                    1,
                    false
                );
            }
            else
            {
                using var cmd = client.RunCommand(finalCommand);
                stdout = cmd.Result;
                stderr = cmd.Error;
                exitCode = cmd.ExitStatus ?? 0;
            }

            client.Disconnect();
            return new DockerExecutionResult(stdout, stderr, exitCode, exitCode == 0);
        }
        catch (OperationCanceledException)
        {
            return new DockerExecutionResult(null, "Operation cancelled.", 1, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH Docker command failed on {Host}: {Command}", remoteHost, command.Args);
            return new DockerExecutionResult(null, ex.Message, 1, false);
        }
    }

    private AuthenticationMethod? BuildAuthMethod(ExecutionHost host)
    {
        return host.AuthType switch
        {
            HostAuthType.PrivateKey when !string.IsNullOrEmpty(host.SshKeyPath)
                => new PrivateKeyAuthenticationMethod(
                    host.User,
                    new PrivateKeyFile(ResolvePath(host.SshKeyPath!))),
            HostAuthType.Password when !string.IsNullOrEmpty(host.SshPassword)
                => new PasswordAuthenticationMethod(host.User, host.SshPassword!),
            _ => null
        };
    }

    private static string ResolvePath(string path)
    {
        return path.StartsWith("~")
            ? path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : path;
    }

    private async Task<(string Stdout, string Stderr, int ExitCode)> ExecuteWithSudoAsync(
        SshClient client, string command, string sudoPassword, CancellationToken ct)
    {
        _logger.LogInformation("Executing sudo Docker command on remote host: {Command}", command);

        try
        {
            var escapedCommand = command.Replace("'", "'\\''");
            var sudoCommand = $"echo '{sudoPassword}' | sudo -S sh -c '{escapedCommand}'";

            using var cmd = client.RunCommand(sudoCommand);
            await Task.Run(() => { }, ct);

            var stdout = cmd.Result;
            var stderr = cmd.Error;
            var exitCode = cmd.ExitStatus ?? 0;

            if (exitCode != 0)
            {
                _logger.LogWarning("Sudo Docker command failed with exit code {ExitCode}: {Stderr}", exitCode, stderr);
            }

            return (stdout, stderr, exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute sudo Docker command: {Command}", command);
            return (string.Empty, ex.Message, 1);
        }
    }

    private static string EscapeBashCommand(string command)
    {
        return command
            .Replace("\\", "\\\\")
            .Replace("'", @"\'")
            .Replace("`", "\\`");
    }

    private static bool IsLocalHost(string host)
    {
        return host is "localhost" or "127.0.0.1" or "::1";
    }
}
