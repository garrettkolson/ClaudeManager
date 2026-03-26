namespace ClaudeManager.Hub.Services;

/// <summary>
/// Bound from the "SweAf" section of appsettings.json.
/// Example:
///   "SweAf": {
///     "BaseUrl": "http://localhost:8080",
///     "ApiKey": "your-agentfield-api-key",
///     "WebhookSecret": "optional-hmac-secret"
///   }
/// </summary>
public record SweAfConfig
{
    public string  BaseUrl       { get; init; } = string.Empty;
    public string  ApiKey        { get; init; } = string.Empty;
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// Optional Anthropic API base URL passed to AgentField in the build trigger payload.
    /// Use this to direct SWE-AF's claude_code runtime at a locally-hosted LLM server.
    /// </summary>
    public string? ClaudeBaseUrl { get; init; }

    /// <summary>
    /// Optional API key passed to AgentField alongside ClaudeBaseUrl.
    /// </summary>
    public string? ClaudeApiKey { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
