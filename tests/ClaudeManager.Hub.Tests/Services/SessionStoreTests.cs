using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Moq;

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class SessionStoreTests
{
    private Mock<IPersistenceQueue> _queueMock = default!;
    private SessionStore _store = default!;
    private DashboardNotifier _notifier = default!;

    [SetUp]
    public void SetUp()
    {
        _queueMock = new Mock<IPersistenceQueue>();
        (_store, _notifier) = new SessionStoreBuilder()
            .WithQueue(_queueMock.Object)
            .Build();
    }

    // ── RegisterAgent ────────────────────────────────────────────────────────

    [Test]
    public void RegisterAgent_NewMachine_ReturnsMachineAgent()
    {
        var agent = _store.RegisterTestAgent();
        agent.Should().NotBeNull();
        agent.MachineId.Should().Be(TestData.MachineId);
    }

    [Test]
    public void RegisterAgent_NewMachine_SetsIsOnlineTrue()
    {
        var agent = _store.RegisterTestAgent();
        agent.IsOnline.Should().BeTrue();
    }

    [Test]
    public void RegisterAgent_NewMachine_FiresAgentConnectedEvent()
    {
        MachineAgent? fired = null;
        _notifier.AgentConnected += a => fired = a;

        _store.RegisterTestAgent();

        fired.Should().NotBeNull();
        fired!.MachineId.Should().Be(TestData.MachineId);
    }

    [Test]
    public void RegisterAgent_NewMachine_EnqueuesUpsertAgent()
    {
        _store.RegisterTestAgent();

        _queueMock.Verify(q => q.EnqueueUpsertAgent(
            It.Is<MachineAgentEntity>(e => e.MachineId == TestData.MachineId)), Times.Once);
    }

    [Test]
    public void RegisterAgent_ExistingMachine_UpdatesConnectionId()
    {
        _store.RegisterAgent(TestData.MachineId, "old-conn", "Test", "win32");
        var agent = _store.RegisterAgent(TestData.MachineId, "new-conn", "Test", "win32");

        agent.SignalRConnectionId.Should().Be("new-conn");
    }

    [Test]
    public void RegisterAgent_ExistingMachine_KeepsExistingSessions()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.RegisterTestAgent(); // re-register

        var machine = _store.GetMachine(TestData.MachineId)!;
        machine.Sessions.Should().ContainKey(TestData.SessionId);
    }

    [Test]
    public void RegisterAgent_ExistingMachine_FiresAgentConnectedEvent()
    {
        _store.RegisterTestAgent();
        int count = 0;
        _notifier.AgentConnected += _ => count++;

        _store.RegisterTestAgent();

        count.Should().Be(1);
    }

    // ── MarkAgentDisconnected ─────────────────────────────────────────────────

    [Test]
    public void MarkAgentDisconnected_SetsIsOnlineFalse()
    {
        _store.RegisterTestAgent();
        _store.MarkAgentDisconnected(TestData.MachineId);

        _store.GetMachine(TestData.MachineId)!.IsOnline.Should().BeFalse();
    }

    [Test]
    public void MarkAgentDisconnected_MarksActiveSessionsAsDisconnected()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.MarkAgentDisconnected(TestData.MachineId);

        var session = _store.GetSession(TestData.MachineId, TestData.SessionId)!;
        session.Status.Should().Be(SessionStatus.Disconnected);
    }

    [Test]
    public void MarkAgentDisconnected_DoesNotTouchEndedSessions()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);
        _store.EndSession(TestData.MachineId, TestData.SessionId, 0);

        _store.MarkAgentDisconnected(TestData.MachineId);

        var session = _store.GetSession(TestData.MachineId, TestData.SessionId)!;
        session.Status.Should().Be(SessionStatus.Ended);
    }

    [Test]
    public void MarkAgentDisconnected_SetsIsProcessRunningFalseOnActiveSessions()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.MarkAgentDisconnected(TestData.MachineId);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.IsProcessRunning.Should().BeFalse();
    }

    [Test]
    public void MarkAgentDisconnected_EnqueuesStatusChangeForEachActiveSession()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, "sess-1", TestData.WorkingDir, null);
        _store.StartSession(TestData.MachineId, "sess-2", TestData.WorkingDir, null);

        _store.MarkAgentDisconnected(TestData.MachineId);

        _queueMock.Verify(q => q.EnqueueSessionStatusChange(
            It.IsAny<string>(), SessionStatus.Disconnected, null), Times.Exactly(2));
    }

    [Test]
    public void MarkAgentDisconnected_FiresAgentDisconnectedEvent()
    {
        _store.RegisterTestAgent();
        string? firedId = null;
        _notifier.AgentDisconnected += id => firedId = id;

        _store.MarkAgentDisconnected(TestData.MachineId);

        firedId.Should().Be(TestData.MachineId);
    }

    [Test]
    public void MarkAgentDisconnected_UnknownMachineId_DoesNotThrow()
    {
        Action act = () => _store.MarkAgentDisconnected("unknown-machine");
        act.Should().NotThrow();
    }

    // ── StartSession ─────────────────────────────────────────────────────────

    [Test]
    public void StartSession_KnownMachine_ReturnsSession()
    {
        _store.RegisterTestAgent();
        var session = _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        session.Should().NotBeNull();
        session.SessionId.Should().Be(TestData.SessionId);
    }

    [Test]
    public void StartSession_KnownMachine_SetsStatusActive()
    {
        _store.RegisterTestAgent();
        var session = _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        session.Status.Should().Be(SessionStatus.Active);
    }

    [Test]
    public void StartSession_KnownMachine_SetsIsProcessRunningTrue()
    {
        _store.RegisterTestAgent();
        var session = _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        session.IsProcessRunning.Should().BeTrue();
    }

    [Test]
    public void StartSession_KnownMachine_EnqueuesUpsertSession()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _queueMock.Verify(q => q.EnqueueUpsertSession(
            It.Is<ClaudeSessionEntity>(e => e.SessionId == TestData.SessionId)), Times.Once);
    }

    [Test]
    public void StartSession_KnownMachine_FiresSessionStartedEvent()
    {
        _store.RegisterTestAgent();
        ClaudeSession? fired = null;
        _notifier.SessionStarted += s => fired = s;

        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        fired.Should().NotBeNull();
        fired!.SessionId.Should().Be(TestData.SessionId);
    }

    [Test]
    public void StartSession_KnownMachine_TruncatesInitialPromptAt4000Chars()
    {
        _store.RegisterTestAgent();
        var longPrompt = new string('x', 5000);
        ClaudeSessionEntity? captured = null;
        _queueMock.Setup(q => q.EnqueueUpsertSession(It.IsAny<ClaudeSessionEntity>()))
            .Callback<ClaudeSessionEntity>(e => captured = e);

        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, longPrompt);

        captured!.InitialPrompt.Should().HaveLength(4000);
    }

    [Test]
    public void StartSession_InitialPromptExactly4000Chars_NotTruncated()
    {
        _store.RegisterTestAgent();
        var prompt4000 = new string('x', 4000);
        ClaudeSessionEntity? captured = null;
        _queueMock.Setup(q => q.EnqueueUpsertSession(It.IsAny<ClaudeSessionEntity>()))
            .Callback<ClaudeSessionEntity>(e => captured = e);

        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, prompt4000);

        captured!.InitialPrompt.Should().HaveLength(4000);
    }

    [Test]
    public void StartSession_InitialPromptNull_EnqueuesNullInitialPrompt()
    {
        _store.RegisterTestAgent();
        ClaudeSessionEntity? captured = null;
        _queueMock.Setup(q => q.EnqueueUpsertSession(It.IsAny<ClaudeSessionEntity>()))
            .Callback<ClaudeSessionEntity>(e => captured = e);

        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        captured!.InitialPrompt.Should().BeNull();
    }

    [Test]
    public void StartSession_UnknownMachine_ThrowsInvalidOperationException()
    {
        Action act = () => _store.StartSession("unknown", TestData.SessionId, TestData.WorkingDir, null);
        act.Should().Throw<InvalidOperationException>();
    }

    // ── AppendLine ────────────────────────────────────────────────────────────

    [Test]
    public void AppendLine_KnownSession_DelegatesToClaudeSessionAppendLine()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.AppendLine(TestData.MachineId, TestData.SessionId, TestData.Line());

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.OutputLines.Should().HaveCount(1);
    }

    [Test]
    public void AppendLine_KnownSession_EnqueuesLine()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.AppendLine(TestData.MachineId, TestData.SessionId, TestData.Line());

        _queueMock.Verify(q => q.EnqueueLine(It.IsAny<StreamedLineEntity>()), Times.Once);
    }

    [Test]
    public void AppendLine_KnownSession_FiresLineStreamedEvent()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);
        StreamedLine? fired = null;
        _notifier.LineStreamed += l => fired = l;

        _store.AppendLine(TestData.MachineId, TestData.SessionId, TestData.Line());

        fired.Should().NotBeNull();
    }

    [Test]
    public void AppendLine_UnknownMachine_SilentlyIgnores()
    {
        Action act = () => _store.AppendLine("unknown", TestData.SessionId, TestData.Line());
        act.Should().NotThrow();
        _queueMock.Verify(q => q.EnqueueLine(It.IsAny<StreamedLineEntity>()), Times.Never);
    }

    [Test]
    public void AppendLine_UnknownSession_SilentlyIgnores()
    {
        _store.RegisterTestAgent();
        Action act = () => _store.AppendLine(TestData.MachineId, "unknown-session", TestData.Line());
        act.Should().NotThrow();
        _queueMock.Verify(q => q.EnqueueLine(It.IsAny<StreamedLineEntity>()), Times.Never);
    }

    // ── EndSession ────────────────────────────────────────────────────────────

    [Test]
    public void EndSession_KnownSession_SetsStatusEnded()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.EndSession(TestData.MachineId, TestData.SessionId, 0);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.Status.Should().Be(SessionStatus.Ended);
    }

    [Test]
    public void EndSession_KnownSession_SetsIsProcessRunningFalse()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.EndSession(TestData.MachineId, TestData.SessionId, 0);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.IsProcessRunning.Should().BeFalse();
    }

    [Test]
    public void EndSession_KnownSession_SetsEndedAt()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.EndSession(TestData.MachineId, TestData.SessionId, 0);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.EndedAt.Should().NotBeNull();
    }

    [Test]
    public void EndSession_KnownSession_EnqueuesSessionStatusChange()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.EndSession(TestData.MachineId, TestData.SessionId, 0);

        _queueMock.Verify(q => q.EnqueueSessionStatusChange(
            TestData.SessionId, SessionStatus.Ended, It.IsNotNull<DateTimeOffset?>()), Times.Once);
    }

    [Test]
    public void EndSession_KnownSession_FiresSessionEndedEvent()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);
        string? firedSession = null;
        _notifier.SessionEnded += (_, sid, _) => firedSession = sid;

        _store.EndSession(TestData.MachineId, TestData.SessionId, 0);

        firedSession.Should().Be(TestData.SessionId);
    }

    [Test]
    public void EndSession_UnknownSession_SilentlyIgnores()
    {
        _store.RegisterTestAgent();
        Action act = () => _store.EndSession(TestData.MachineId, "unknown-session", 0);
        act.Should().NotThrow();
    }

    // ── SetProcessRunning ─────────────────────────────────────────────────────

    [Test]
    public void SetProcessRunning_True_UpdatesFlag()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);
        _store.EndSession(TestData.MachineId, TestData.SessionId, 0); // sets to false

        _store.SetProcessRunning(TestData.MachineId, TestData.SessionId, true);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.IsProcessRunning.Should().BeTrue();
    }

    [Test]
    public void SetProcessRunning_False_UpdatesFlag()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        _store.SetProcessRunning(TestData.MachineId, TestData.SessionId, false);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.IsProcessRunning.Should().BeFalse();
    }

    [Test]
    public void SetProcessRunning_FiresSessionStatusChangedEvent()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);
        ClaudeSession? fired = null;
        _notifier.SessionStatusChanged += s => fired = s;

        _store.SetProcessRunning(TestData.MachineId, TestData.SessionId, false);

        fired.Should().NotBeNull();
    }

    [Test]
    public void SetProcessRunning_UnknownSession_SilentlyIgnores()
    {
        _store.RegisterTestAgent();
        Action act = () => _store.SetProcessRunning(TestData.MachineId, "unknown", true);
        act.Should().NotThrow();
    }

    // ── GetConnectionId ───────────────────────────────────────────────────────

    [Test]
    public void GetConnectionId_OnlineMachine_ReturnsConnectionId()
    {
        _store.RegisterTestAgent();
        _store.GetConnectionId(TestData.MachineId).Should().Be(TestData.ConnectionId);
    }

    [Test]
    public void GetConnectionId_OfflineMachine_ReturnsNull()
    {
        _store.RegisterTestAgent();
        _store.MarkAgentDisconnected(TestData.MachineId);

        _store.GetConnectionId(TestData.MachineId).Should().BeNull();
    }

    [Test]
    public void GetConnectionId_UnknownMachine_ReturnsNull()
    {
        _store.GetConnectionId("unknown").Should().BeNull();
    }

    // ── DB recovery helpers ───────────────────────────────────────────────────

    [Test]
    public void EnsureAgentFromDb_NewMachine_AddsOfflineAgent()
    {
        _store.EnsureAgentFromDb(TestData.AgentEntity());

        var machine = _store.GetMachine(TestData.MachineId);
        machine.Should().NotBeNull();
        machine!.IsOnline.Should().BeFalse();
    }

    [Test]
    public void EnsureAgentFromDb_ExistingMachine_DoesNotOverwrite()
    {
        _store.RegisterTestAgent(); // registers as online
        _store.EnsureAgentFromDb(TestData.AgentEntity()); // TryAdd should be no-op

        // Machine should still be online (not overwritten by offline entity)
        _store.GetMachine(TestData.MachineId)!.IsOnline.Should().BeTrue();
    }

    [Test]
    public void RestoreSessionFromDb_KnownMachine_AddsSession()
    {
        _store.EnsureAgentFromDb(TestData.AgentEntity());

        _store.RestoreSessionFromDb(TestData.SessionEntity(), []);

        _store.GetSession(TestData.MachineId, TestData.SessionId).Should().NotBeNull();
    }

    [Test]
    public void RestoreSessionFromDb_KnownMachine_SetsOutputLines()
    {
        _store.EnsureAgentFromDb(TestData.AgentEntity());
        var lines = new[] { TestData.Line(), TestData.Line() };

        _store.RestoreSessionFromDb(TestData.SessionEntity(), lines);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.OutputLines.Should().HaveCount(2);
    }

    [Test]
    public void RestoreSessionFromDb_KnownMachine_SetsIsProcessRunningFalse()
    {
        _store.EnsureAgentFromDb(TestData.AgentEntity());

        _store.RestoreSessionFromDb(TestData.SessionEntity(), []);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!.IsProcessRunning.Should().BeFalse();
    }

    [Test]
    public void RestoreSessionFromDb_UnknownMachine_SilentlyIgnores()
    {
        Action act = () => _store.RestoreSessionFromDb(TestData.SessionEntity(), []);
        act.Should().NotThrow();
    }
}
