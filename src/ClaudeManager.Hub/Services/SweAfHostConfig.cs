namespace ClaudeManager.Hub.Services;

/// <summary>
/// SSH connection details and service control commands for the machine running AgentField.
/// Configured under "SweAfHost" in appsettings.json.
/// Leave unconfigured (omit the section) to disable host service controls.
/// </summary>
public record SweAfHostConfig
{
    /// <summary>
    /// Hostname or IP of the SWE-AF host.
    /// Use "localhost" or "127.0.0.1" to run commands locally via Process.Start instead of SSH.
    /// </summary>
    public string  Host         { get; init; } = string.Empty;

    /// <summary>SSH port. Defaults to 22.</summary>
    public int     Port         { get; init; } = 22;

    /// <summary>SSH username. Not required for localhost.</summary>
    public string  SshUser      { get; init; } = string.Empty;

    /// <summary>Path to SSH private key file. Supports ~ expansion.</summary>
    public string? SshKeyPath   { get; init; }

    /// <summary>SSH password. Prefer key-based auth.</summary>
    public string? SshPassword  { get; init; }

    /// <summary>Shell command to start the AgentField service (e.g. "sudo systemctl start agentfield").</summary>
    public string  StartCommand { get; init; } = string.Empty;

    /// <summary>Shell command to stop the AgentField service (e.g. "sudo systemctl stop agentfield").</summary>
    public string  StopCommand  { get; init; } = string.Empty;

    /// <summary>True when both start and stop commands are present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(StartCommand) &&
        !string.IsNullOrWhiteSpace(StopCommand);
}
