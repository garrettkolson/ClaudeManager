using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Singleton service that listens to build and session events and raises
/// <see cref="ToastRequested"/> for each notification-worthy state change.
/// The Blazor <c>ToastContainer</c> component subscribes to this event and
/// handles both in-app toasts and browser push notifications via JS interop.
/// </summary>
public class NotificationService
{
    private readonly SessionStore _sessions;

    // Tracks (jobId, status) pairs we have already notified about so that
    // re-fires of BuildChanged for the same terminal status don't double-notify.
    private readonly HashSet<(long, BuildStatus)> _notifiedBuilds = new();
    private readonly object _lock = new();

    public event Action<ToastMessage>? ToastRequested;

    public NotificationService(
        BuildNotifier     buildNotifier,
        DashboardNotifier dashboardNotifier,
        SessionStore      sessions)
    {
        _sessions = sessions;
        buildNotifier.BuildChanged     += OnBuildChanged;
        dashboardNotifier.SessionEnded += OnSessionEnded;
    }

    private void OnBuildChanged(SweAfJobEntity job)
    {
        if (job.Status is not (BuildStatus.Succeeded or BuildStatus.Failed
                               or BuildStatus.Waiting  or BuildStatus.Cancelled))
            return;

        lock (_lock)
        {
            if (!_notifiedBuilds.Add((job.Id, job.Status)))
                return;
        }

        var (title, kind) = job.Status switch
        {
            BuildStatus.Succeeded => ("Build complete",    ToastKind.Success),
            BuildStatus.Failed    => ("Build failed",      ToastKind.Error),
            BuildStatus.Waiting   => ("Approval needed",   ToastKind.Warning),
            BuildStatus.Cancelled => ("Build cancelled",   ToastKind.Info),
            _                     => ("Build updated",     ToastKind.Info),
        };

        var body = job.Goal.Length <= 60 ? job.Goal : job.Goal[..60] + "…";
        ToastRequested?.Invoke(ToastMessage.Create(title, body, kind));
    }

    private void OnSessionEnded(string machineId, string sessionId, int exitCode)
    {
        var machine     = _sessions.GetAllMachines().FirstOrDefault(m => m.MachineId == machineId);
        var machineName = machine?.DisplayName ?? machineId;

        var (title, body, kind) = exitCode == 0
            ? ($"Session complete — {machineName}", "Session ended normally.", ToastKind.Info)
            : ($"Session error — {machineName}",    $"Exited with code {exitCode}.", ToastKind.Error);

        ToastRequested?.Invoke(ToastMessage.Create(title, body, kind));
    }
}
