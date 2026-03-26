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

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
