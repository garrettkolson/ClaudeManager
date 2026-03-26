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

    /// <summary>
    /// Overrides the Anthropic API base URL for every claude process spawned by this agent.
    /// Passed as the ANTHROPIC_BASE_URL environment variable.
    /// Use this to point claude at a locally-hosted LLM server.
    /// Null uses claude's default (api.anthropic.com).
    /// </summary>
    public string? ClaudeBaseUrl { get; set; }

    /// <summary>
    /// Overrides the API key passed to claude. Passed as ANTHROPIC_API_KEY.
    /// Typically needed alongside ClaudeBaseUrl when the local server uses a
    /// different key (or a placeholder key such as "local").
    /// Null inherits the key from the agent process environment.
    /// </summary>
    public string? ClaudeApiKey { get; set; }
}
