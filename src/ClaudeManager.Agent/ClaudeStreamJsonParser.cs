namespace ClaudeManager.Agent;

/// <summary>
/// Parses fields from claude's --output-format stream-json lines.
/// Extracted as a public static class so it can be tested independently of ClaudeProcess.
/// All methods operate on raw JSON strings without a full JSON parse.
/// </summary>
public static class ClaudeStreamJsonParser
{
    /// <summary>
    /// Extracts the session_id value from a system/init line.
    /// Returns null if the field is absent or malformed.
    /// </summary>
    public static string? ExtractSessionId(string json)
    {
        const string marker = "\"session_id\":\"";
        var idx = json.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var start = idx + marker.Length;
        var end   = json.IndexOf('"', start);
        return end > start ? json[start..end] : null;
    }

    /// <summary>
    /// Returns true if the JSON line looks like a result/error response,
    /// which on the very first line of a --resume session indicates the session was not found.
    /// </summary>
    public static bool IsResumeError(string json) =>
        json.Contains("\"type\":\"result\"", StringComparison.Ordinal) &&
        json.Contains("\"subtype\":\"error\"", StringComparison.Ordinal);
}
