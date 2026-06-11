namespace ClaudeManager.Hub.Services;

public record ErrorTriggerConfig
{
    /// <summary>
    /// Environment identifier for RabbitMQConnService: "local", "dev", "prod", or "localrabbit".
    /// Use "localrabbit" for local dev with guest/guest auth.
    /// </summary>
    public string? EnvironmentIs { get; init; } = "localrabbit";

    public string ExchangeName { get; init; } = "ex_errors";
    public string QueueName { get; init; } = "hub_error_triggers";
    public string RoutingKey { get; init; } = "error";

    /// <summary>Build goal template. Supports {ErrorDescription} and {ErrorMetadata} placeholders.</summary>
    public string BuildGoalTemplate { get; init; } = "Fix error: {ErrorDescription}";

    /// <summary>Whether error-triggered builds require human approval (default true).</summary>
    public bool RequireApproval { get; init; } = true;
}
