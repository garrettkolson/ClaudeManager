namespace ClaudeManager.Hub.Services;

public record ErrorTriggerInnerException
{
    public string? ClassName { get; init; }
    public string? Message { get; init; }
    public string? StackTraceString { get; init; }
    public string? Source { get; init; }
}

public record ErrorTriggerMessage
{
    public string? ExceptionType { get; init; }
    public ErrorTriggerInnerException? Exception { get; init; }
    public object? Arguments { get; init; }
    public object? Metadata { get; init; }
}
