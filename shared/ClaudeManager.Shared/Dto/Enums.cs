namespace ClaudeManager.Shared.Dto;

public enum StreamLineKind
{
    AssistantToken = 0,
    ToolUse        = 1,
    ToolResult     = 2,
    ResultSummary  = 3,
    Error          = 4,
    ProcessExit    = 5,
}

public enum SessionStatus
{
    Active       = 0,
    Ended        = 1,
    Disconnected = 2,
}
