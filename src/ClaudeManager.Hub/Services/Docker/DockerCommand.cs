namespace ClaudeManager.Hub.Services.Docker;

/// <summary>
/// Represents a Docker command that can be built and executed locally or via SSH.
/// </summary>
public record DockerCommand(
    string Args,             // Docker CLI arguments (without "docker" prefix)
    bool RequiresSudo = false,
    string? SudoPassword = null,
    string? WorkingDir = null
)
{
    /// <summary>
    /// Creates a DockerCommand from a string.
    /// </summary>
    public static DockerCommand FromString(string args) => new(Args: args);

    /// <summary>
    /// Creates a DockerCommand from parsed arguments.
    /// </summary>
    public static DockerCommand FromArgs(string[] args) => new(
        Args: string.Join(" ", args),
        RequiresSudo: false,
        SudoPassword: null
    );
};
