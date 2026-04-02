namespace ClaudeManager.Hub.Services.Docker;

/// <summary>
/// Represents a Docker command that can be built and executed locally or via SSH.
/// </summary>
public record DockerCommand(
    string[] Args,           // Docker CLI arguments (without "docker" prefix)
    bool RequiresSudo = false,
    string? SudoPassword = null,
    string? WorkingDir = null
);
