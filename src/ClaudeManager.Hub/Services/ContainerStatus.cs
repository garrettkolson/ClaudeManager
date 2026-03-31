namespace ClaudeManager.Hub.Services;

public enum ContainerStatus
{
    Running,
    Exited,
    Dead,  // Started then crashed, exited with non-zero
    Unknown,
}
