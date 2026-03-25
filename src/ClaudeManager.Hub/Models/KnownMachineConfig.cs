namespace ClaudeManager.Hub.Models;

/// <summary>
/// Configuration for a machine that is known to the hub even before its agent connects.
/// Defined in appsettings.json under the "KnownMachines" array.
/// </summary>
public class KnownMachineConfig
{
    /// <summary>Stable identifier — must match the MachineId the agent reports on registration.</summary>
    public string MachineId { get; set; } = default!;

    /// <summary>Human-readable label shown in the dashboard.</summary>
    public string DisplayName { get; set; } = default!;

    /// <summary>Platform hint for the dashboard icon ("win32", "darwin", "linux"). Optional.</summary>
    public string Platform { get; set; } = "linux";

    // ── SSH connection ────────────────────────────────────────────────────────

    /// <summary>
    /// Hostname or IP of the remote machine. Use "localhost" or "127.0.0.1" to
    /// launch the agent on the same machine as the Hub via Process.Start instead of SSH.
    /// </summary>
    public string Host { get; set; } = default!;

    /// <summary>SSH port. Defaults to 22.</summary>
    public int Port { get; set; } = 22;

    /// <summary>SSH username.</summary>
    public string SshUser { get; set; } = default!;

    /// <summary>
    /// Path to the SSH private key file. Supports ~ expansion.
    /// Either SshKeyPath or SshPassword must be provided for remote machines.
    /// </summary>
    public string? SshKeyPath { get; set; }

    /// <summary>SSH password. Prefer key-based auth for security.</summary>
    public string? SshPassword { get; set; }

    // ── Agent launch ──────────────────────────────────────────────────────────

    /// <summary>
    /// Full command to run on the remote machine, including any detach syntax.
    /// Examples:
    ///   Linux/macOS:  "nohup /opt/claude-manager/ClaudeManager.Agent &gt; /dev/null 2&gt;&amp;1 &amp;"
    ///   Windows SSH:  "powershell -Command \"Start-Process C:\\Tools\\ClaudeManager.Agent.exe -WindowStyle Hidden\""
    ///   Localhost:    "dotnet /opt/claude-manager/ClaudeManager.Agent.dll"
    /// </summary>
    public string AgentCommand { get; set; } = default!;
}
