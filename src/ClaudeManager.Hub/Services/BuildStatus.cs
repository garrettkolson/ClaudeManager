namespace ClaudeManager.Hub.Services;

public enum BuildStatus
{
    Queued    = 0,
    Running   = 1,
    Waiting   = 2,  // human-in-the-loop pause
    Succeeded = 3,
    Failed    = 4,
    Cancelled = 5,
}
