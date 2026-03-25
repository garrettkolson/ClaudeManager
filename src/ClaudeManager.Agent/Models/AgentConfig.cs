namespace ClaudeManager.Agent.Models;

public class AgentConfig
{
    public string HubUrl { get; set; } = default!;
    public string SharedSecret { get; set; } = default!;
    public string? DisplayName { get; set; }
    public string? ClaudeBinaryPath { get; set; }   // null = resolve from PATH
    public string DefaultWorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Full path to the ClaudeManager.McpServer executable.
    /// When set, the agent generates a per-startup MCP config and passes --mcp-config
    /// to every claude invocation so Claude can call wiki_save / wiki_list.
    /// Null disables MCP wiki tools.
    /// </summary>
    public string? McpServerPath { get; set; }
}
