namespace ClaudeManager.Agent.Models;

public class AgentConfig
{
    public string HubUrl { get; set; } = default!;
    public string SharedSecret { get; set; } = default!;
    public string? DisplayName { get; set; }
    public string? ClaudeBinaryPath { get; set; }   // null = resolve from PATH
    public string DefaultWorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
