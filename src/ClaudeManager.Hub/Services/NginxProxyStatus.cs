namespace ClaudeManager.Hub.Services;

public enum NginxProxyStatus
{
    Unknown  = 0,
    NotFound = 1,   // container has never been started
    Running  = 2,
    Stopped  = 3,
    Error    = 4,
}
