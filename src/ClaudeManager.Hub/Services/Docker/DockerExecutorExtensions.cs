namespace ClaudeManager.Hub.Services.Docker;

using ClaudeManager.Hub.Persistence.Entities;

/// <summary>
/// Extension methods to help migrate from legacy execution patterns to the new DockerExecutor.
/// </summary>
public static class DockerExecutorExtensions
{
    /// <summary>
    /// Converts a GpuHostEntity to an ExecutionHost for use with DockerExecutor.
    /// </summary>
    public static ExecutionHost ToExecutionHost(this GpuHostEntity host)
    {
        var authMode = DetermineAuthType(host);
        return new ExecutionHost(
            Host: host.Host,
            Port: host.SshPort,
            User: host.SshUser ?? "root",
            AuthType: authMode,
            SshKeyPath: host.SshKeyPath,
            SshPassword: host.SshPassword,
            SudoPassword: host.RequiresSudo ? host.SudoPassword : null
        );
    }

    /// <summary>
    /// Converts a SweAfConfigEntity to an ExecutionHost for use with DockerExecutor.
    /// </summary>
    public static ExecutionHost ToExecutionHost(this SweAfConfigEntity config)
    {
        var authMode = DetermineAuthType(config);
        return new ExecutionHost(
            Host: config.ProvisionHost ?? "localhost",
            Port: config.SshPort,
            User: config.SshUser ?? "root",
            AuthType: authMode,
            SshKeyPath: !string.IsNullOrEmpty(config.SshKeyPath) ? config.SshKeyPath : null,
            SshPassword: !string.IsNullOrEmpty(config.SshPassword) ? config.SshPassword : null,
            SudoPassword: config.RequiresSudo ? config.SudoPassword : null
        );
    }

    /// <summary>
    /// Helper to convert legacy (Stdout, Stderr, ExitCode) tuples to DockerExecutionResult.
    /// </summary>
    public static DockerExecutionResult ToDockerExecutionResult(this (string? Stdout, string? Stderr, int ExitCode) legacy)
    {
        return new DockerExecutionResult(
            legacy.Stdout,
            legacy.Stderr,
            legacy.ExitCode,
            legacy.ExitCode == 0
        );
    }

    /// <summary>
    /// Helper to convert DockerExecutionResult to legacy tuple format.
    /// </summary>
    public static (string? Stdout, string? Stderr, int ExitCode) ToLegacy(this DockerExecutionResult result)
    {
        return (result.Stdout, result.Stderr, result.ExitCode);
    }

    /// <summary>
    /// Determines the authentication mode from a host entity.
    /// </summary>
    private static HostAuthType DetermineAuthType(GpuHostEntity host)
    {
        if (!string.IsNullOrEmpty(host.SshKeyPath))
            return HostAuthType.PrivateKey;
        if (!string.IsNullOrEmpty(host.SshPassword))
            return HostAuthType.Password;
        return HostAuthType.None;
    }

    /// <summary>
    /// Determines the authentication mode from a config entity.
    /// </summary>
    private static HostAuthType DetermineAuthType(SweAfConfigEntity config)
    {
        if (!string.IsNullOrEmpty(config.SshKeyPath))
            return HostAuthType.PrivateKey;
        if (!string.IsNullOrEmpty(config.SshPassword))
            return HostAuthType.Password;
        return HostAuthType.None;
    }
}
