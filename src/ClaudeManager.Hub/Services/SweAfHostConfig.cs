namespace ClaudeManager.Hub.Services;

/// <summary>
/// A named shell command shown as a button on the Builds page.
/// </summary>
public record SweAfHostCommand
{
    /// <summary>Button label displayed in the UI (e.g. "Start", "Stop", "Update &amp; Restart").</summary>
    public string Label   { get; init; } = string.Empty;

    /// <summary>Shell command executed on the host when the button is clicked.</summary>
    public string Command { get; init; } = string.Empty;
}

/// <summary>
/// SSH connection details and configurable service commands for the machine running AgentField.
/// Configured under "SweAfHost" in appsettings.json.
/// Leave unconfigured (omit the section) to disable host service controls.
/// </summary>
public record SweAfHostConfig
{
    /// <summary>
    /// Hostname or IP of the SWE-AF host.
    /// Use "localhost" or "127.0.0.1" to run commands locally via Process.Start instead of SSH.
    /// </summary>
    public string  Host       { get; init; } = string.Empty;

    /// <summary>SSH port. Defaults to 22.</summary>
    public int     Port       { get; init; } = 22;

    /// <summary>SSH username. Not required for localhost.</summary>
    public string  SshUser    { get; init; } = string.Empty;

    /// <summary>Path to SSH private key file. Supports ~ expansion.</summary>
    public string? SshKeyPath  { get; init; }

    /// <summary>SSH password. Prefer key-based auth.</summary>
    public string? SshPassword { get; init; }

    /// <summary>
    /// Named commands shown as buttons on the Builds page.
    /// Each entry becomes one button; clicking it runs the shell command on the host.
    /// </summary>
    public List<SweAfHostCommand>? Commands { get; init; }

    /// <summary>
    /// Optional Anthropic API base URL set as ANTHROPIC_BASE_URL when running commands.
    /// For localhost: set directly on the child process environment.
    /// For SSH / bare metal: prepended as an inline shell assignment before the command.
    /// For Docker Compose: inline env vars cannot inject into containers — set ANTHROPIC_BASE_URL
    /// in the .env file on the host machine instead.
    /// </summary>
    public string? AnthropicBaseUrl { get; init; }

    /// <summary>Optional API key set as ANTHROPIC_API_KEY alongside AnthropicBaseUrl.</summary>
    public string? AnthropicApiKey  { get; init; }

    /// <summary>True when at least one command is configured.</summary>
    public bool IsConfigured => Commands is { Count: > 0 };
}
