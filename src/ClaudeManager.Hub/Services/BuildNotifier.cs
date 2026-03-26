using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

public class BuildNotifier
{
    public event Action<SweAfJobEntity>? BuildChanged;

    public void NotifyBuildChanged(SweAfJobEntity job) => BuildChanged?.Invoke(job);
}
