namespace ClaudeManager.Hub.Services;

public enum LlmDeploymentStatus
{
    Stopped  = 0,
    Starting = 1,
    Running  = 2,
    Error    = 3,
}
