using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using FluentAssertions;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class NotificationServiceTests
{
    private BuildNotifier     _buildNotifier     = default!;
    private DashboardNotifier _dashboardNotifier = default!;
    private SessionStore      _sessionStore      = default!;
    private NotificationService _svc             = default!;

    [SetUp]
    public void SetUp()
    {
        _buildNotifier     = new BuildNotifier();
        var (store, dash)  = new SessionStoreBuilder().Build();
        _sessionStore      = store;
        _dashboardNotifier = dash;
        _svc = new NotificationService(_buildNotifier, _dashboardNotifier, _sessionStore);
    }

    // ── Build notifications ───────────────────────────────────────────────────

    [Test]
    public void BuildSucceeded_RaisesToastWithSuccessKind()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Succeeded));

        toast.Should().NotBeNull();
        toast!.Kind.Should().Be(ToastKind.Success);
        toast.Title.Should().Contain("complete");
    }

    [Test]
    public void BuildFailed_RaisesToastWithErrorKind()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Failed));

        toast!.Kind.Should().Be(ToastKind.Error);
        toast.Title.Should().Contain("failed");
    }

    [Test]
    public void BuildWaiting_RaisesToastWithWarningKind()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Waiting));

        toast!.Kind.Should().Be(ToastKind.Warning);
        toast.Title.Should().Contain("Approval");
    }

    [Test]
    public void BuildCancelled_RaisesToastWithInfoKind()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Cancelled));

        toast!.Kind.Should().Be(ToastKind.Info);
    }

    [Test]
    public void BuildQueued_DoesNotRaiseToast()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Queued));

        toast.Should().BeNull();
    }

    [Test]
    public void BuildRunning_DoesNotRaiseToast()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Running));

        toast.Should().BeNull();
    }

    [Test]
    public void BuildSucceeded_ToastBodyContainsGoal()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Succeeded, goal: "Fix the login bug"));

        toast!.Body.Should().Be("Fix the login bug");
    }

    [Test]
    public void BuildSucceeded_LongGoal_TruncatedTo60Chars()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        var longGoal = new string('x', 80);
        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Succeeded, goal: longGoal));

        toast!.Body.Should().HaveLength(61); // 60 chars + ellipsis
        toast.Body.Should().EndWith("…");
    }

    [Test]
    public void BuildSucceeded_SameJobSameStatus_NotifiesOnlyOnce()
    {
        var toasts = new List<ToastMessage>();
        _svc.ToastRequested += t => toasts.Add(t);

        var job = MakeJob(BuildStatus.Succeeded);
        _buildNotifier.NotifyBuildChanged(job);
        _buildNotifier.NotifyBuildChanged(job); // duplicate

        toasts.Should().HaveCount(1);
    }

    [Test]
    public void BuildSucceeded_ThenFailed_NotifiesTwice()
    {
        var toasts = new List<ToastMessage>();
        _svc.ToastRequested += t => toasts.Add(t);

        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Succeeded));
        _buildNotifier.NotifyBuildChanged(MakeJob(BuildStatus.Failed));

        toasts.Should().HaveCount(2);
    }

    // ── Session notifications ─────────────────────────────────────────────────

    [Test]
    public void SessionEnded_ExitCode0_RaisesInfoToast()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _dashboardNotifier.NotifySessionEnded("machine-1", "sess-1", exitCode: 0);

        toast.Should().NotBeNull();
        toast!.Kind.Should().Be(ToastKind.Info);
        toast.Title.Should().Contain("complete");
    }

    [Test]
    public void SessionEnded_NonZeroExitCode_RaisesErrorToast()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _dashboardNotifier.NotifySessionEnded("machine-1", "sess-1", exitCode: 1);

        toast!.Kind.Should().Be(ToastKind.Error);
        toast.Title.Should().Contain("error");
        toast.Body.Should().Contain("1");
    }

    [Test]
    public void SessionEnded_KnownMachine_UsesDisplayName()
    {
        _sessionStore.RegisterTestAgent(machineId: "m-abc");
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _dashboardNotifier.NotifySessionEnded("m-abc", "sess-1", exitCode: 0);

        toast!.Title.Should().Contain("Test Machine");
    }

    [Test]
    public void SessionEnded_UnknownMachine_UsesMachineId()
    {
        ToastMessage? toast = null;
        _svc.ToastRequested += t => toast = t;

        _dashboardNotifier.NotifySessionEnded("unknown-machine", "sess-1", exitCode: 0);

        toast!.Title.Should().Contain("unknown-machine");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SweAfJobEntity MakeJob(BuildStatus status, string goal = "Implement feature X") =>
        new()
        {
            Id            = 1,
            ExternalJobId = "ext-1",
            Goal          = goal,
            RepoUrl       = "https://github.com/org/repo",
            Status        = status,
            CreatedAt     = DateTimeOffset.UtcNow,
        };
}
