using System.Text.Json.Serialization;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Bound from the "SweAf" section of appsettings.json.
///
/// SWE-AF requires two running services:
///   - AgentField control plane  (agentfield/control-plane Docker image, port 8080)
///   - SWE-AF agent node         (python -m swe_af, port 8003)
///
/// BaseUrl should point at the control plane (e.g. "http://localhost:8080").
///
/// Example (Docker Compose deployment):
///   "SweAf": {
///     "BaseUrl":       "http://localhost:8080",
///     "ApiKey":        "your-agentfield-api-key",
///     "WebhookSecret": "optional-hmac-secret",
///     "Runtime":       "claude_code",
///     "Models": {
///       "Default": "sonnet",
///       "Coder":   "opus"
///     }
///   }
/// </summary>
public record SweAfConfig
{
    public string  BaseUrl       { get; init; } = string.Empty;
    public string  ApiKey        { get; init; } = string.Empty;
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// Runtime backend passed to AgentField in the build trigger payload.
    /// "claude_code" (default) uses Claude Code; "open_code" uses the OpenCode backend
    /// for open-source models (DeepSeek, Qwen, Llama, etc.).
    /// </summary>
    public string Runtime { get; init; } = "claude_code";

    /// <summary>
    /// Per-role model overrides passed to AgentField in the build trigger payload.
    /// Null omits the models field and lets AgentField use its defaults.
    /// </summary>
    public SweAfModelsConfig? Models { get; init; }

    /// <summary>
    /// Optional public URL of this Hub instance (e.g. "https://hub.example.com").
    /// When set, the Builds page pre-populates the webhook registration URL as
    /// <c>{HubPublicUrl}/api/webhooks/agentfield</c>.
    /// Leave unset if the Hub URL is not yet known — the field remains editable.
    /// Do NOT include a trailing slash.
    /// </summary>
    public string? HubPublicUrl { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Per-role model selection for an AgentField execution.
/// Values are model IDs: short names ("sonnet", "opus") for Claude, or
/// "provider/model-id" format ("deepseek/deepseek-chat") for open-source models.
/// Null properties are omitted from the trigger payload.
/// </summary>
public record SweAfModelsConfig
{
    /// <summary>Default model used for most tasks.</summary>
    [JsonPropertyName("default")] public string? Default { get; init; }

    /// <summary>Model used for coding-intensive steps.</summary>
    [JsonPropertyName("coder")]   public string? Coder   { get; init; }

    /// <summary>Model used for QA / verification steps.</summary>
    [JsonPropertyName("qa")]      public string? Qa      { get; init; }
    
    [JsonPropertyName("pm")]      public string? Pm      { get; init; }
    [JsonPropertyName("architect")]      public string? Architect      { get; init; }
    [JsonPropertyName("tech_lead")]      public string? TechLead      { get; init; }
    [JsonPropertyName("sprint_planner")]      public string? SprintPlanner      { get; init; }
    [JsonPropertyName("code_reviewer")]      public string? CodeReviewer      { get; init; }
    [JsonPropertyName("qa_synthesizer")]      public string? QaSynthesizer      { get; init; }
    [JsonPropertyName("replan")]      public string? Replan      { get; init; }
    [JsonPropertyName("retry_advisor")]      public string? RetryAdvisor      { get; init; }
    [JsonPropertyName("issue_writer")]      public string? IssueWriter      { get; init; }
    [JsonPropertyName("issue_advisor")]      public string? IssueAdvisor      { get; init; }
    [JsonPropertyName("verifier")]      public string? Verifier      { get; init; }
    [JsonPropertyName("git")]      public string? Git      { get; init; }
    [JsonPropertyName("merger")]      public string? Merger      { get; init; }
    [JsonPropertyName("integration_tester")]      public string? IntegrationTester      { get; init; }
}
