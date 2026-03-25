namespace ClaudeManager.Agent;

/// <summary>
/// Builds the CLI argument string for a claude -p invocation.
/// Extracted as a public static class so it can be tested independently of ClaudeProcess.
/// </summary>
public static class ClaudeArgumentBuilder
{
    public static string Build(string prompt, string? resumeSessionId, string? mcpConfigPath = null)
    {
        // Escape the prompt for CLI: wrap in double quotes, escape inner quotes and backslashes
        var escaped = prompt.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var args    = $"-p \"{escaped}\" --output-format stream-json";

        if (resumeSessionId is not null)
            args += $" --resume {resumeSessionId}";

        if (mcpConfigPath is not null)
            args += $" --mcp-config \"{mcpConfigPath.Replace("\"", "\\\"")}\"";

        return args;
    }
}
