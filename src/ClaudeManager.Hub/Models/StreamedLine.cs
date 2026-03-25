using ClaudeManager.Shared.Dto;

namespace ClaudeManager.Hub.Models;

public class StreamedLine
{
    public long DbId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string SessionId { get; init; } = default!;
    public StreamLineKind Kind { get; init; }
    public string Content { get; init; } = default!;
    public bool IsContentTruncated { get; init; }
    public string? ToolName { get; init; }
}
