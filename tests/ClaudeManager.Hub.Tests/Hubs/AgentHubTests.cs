using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeManager.Hub.Tests.Hubs;

/// <summary>
/// Tests for AgentHub methods beyond StreamLine: RegisterAgent, SessionStarted,
/// SessionEnded, Heartbeat, OnConnectedAsync, OnDisconnectedAsync.
/// Hub.Context is set via the public setter using a mocked HubCallerContext.
/// </summary>
[TestFixture]
public class AgentHubTests
{
    private SessionStore _store = default!;
    private DashboardNotifier _notifier = default!;
    private AgentHub _hub = default!;
    private IDictionary<object, object?> _contextItems = default!;

    [SetUp]
    public void SetUp()
    {
        (_store, _notifier) = new SessionStoreBuilder().Build();
        _hub = new AgentHub(_store, NullLogger<AgentHub>.Instance);

        _contextItems = new Dictionary<object, object?>();
        var ctxMock = new Mock<HubCallerContext>();
        ctxMock.Setup(c => c.ConnectionId).Returns(TestData.ConnectionId);
        ctxMock.Setup(c => c.Items).Returns(_contextItems);
        _hub.Context = ctxMock.Object;
    }

    [TearDown]
    public void TearDown() => _hub?.Dispose();

    private static AgentRegistration MakeRegistration(string? machineId = null) =>
        new(MachineId:    machineId ?? TestData.MachineId,
            DisplayName:  "Test Machine",
            Platform:     "win32",
            AgentVersion: "1.0.0");

    // ── RegisterAgent ─────────────────────────────────────────────────────────

    [Test]
    public async Task RegisterAgent_StoresMachineIdInHubContextItems()
    {
        await _hub.RegisterAgent(MakeRegistration());

        _contextItems["MachineId"].Should().Be(TestData.MachineId);
    }

    [Test]
    public async Task RegisterAgent_MachineIsOnlineWithCorrectConnectionId()
    {
        await _hub.RegisterAgent(MakeRegistration());

        _store.GetConnectionId(TestData.MachineId).Should().Be(TestData.ConnectionId);
    }

    [Test]
    public async Task RegisterAgent_FiresAgentConnectedEvent()
    {
        MachineAgent? received = null;
        _notifier.AgentConnected += a => received = a;

        await _hub.RegisterAgent(MakeRegistration());

        received.Should().NotBeNull();
        received!.MachineId.Should().Be(TestData.MachineId);
    }

    [Test]
    public async Task RegisterAgent_Reconnect_UpdatesConnectionId()
    {
        await _hub.RegisterAgent(MakeRegistration());

        // Simulate reconnect on a new connection
        var newItems = new Dictionary<object, object?>();
        var newCtx   = new Mock<HubCallerContext>();
        newCtx.Setup(c => c.ConnectionId).Returns("conn-new");
        newCtx.Setup(c => c.Items).Returns(newItems);
        _hub.Context = newCtx.Object;

        await _hub.RegisterAgent(MakeRegistration());

        _store.GetConnectionId(TestData.MachineId).Should().Be("conn-new");
    }

    // ── SessionStarted ────────────────────────────────────────────────────────

    [Test]
    public async Task SessionStarted_CreatesSessionInStore()
    {
        _store.RegisterTestAgent();

        await _hub.SessionStarted(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, "my prompt");

        _store.GetSession(TestData.MachineId, TestData.SessionId).Should().NotBeNull();
    }

    [Test]
    public async Task SessionStarted_PreservesWorkingDirectoryAndPrompt()
    {
        _store.RegisterTestAgent();

        await _hub.SessionStarted(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, "hello claude");

        var session = _store.GetSession(TestData.MachineId, TestData.SessionId)!;
        session.WorkingDirectory.Should().Be(TestData.WorkingDir);
        session.InitialPrompt.Should().Be("hello claude");
    }

    [Test]
    public async Task SessionStarted_FiresSessionStartedEvent()
    {
        _store.RegisterTestAgent();
        ClaudeSession? fired = null;
        _notifier.SessionStarted += s => fired = s;

        await _hub.SessionStarted(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        fired.Should().NotBeNull();
        fired!.SessionId.Should().Be(TestData.SessionId);
    }

    // ── SessionEnded ──────────────────────────────────────────────────────────

    [Test]
    public async Task SessionEnded_ChangesStatusAwayFromActive()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);

        await _hub.SessionEnded(TestData.MachineId, TestData.SessionId, 0);

        _store.GetSession(TestData.MachineId, TestData.SessionId)!
              .Status.Should().NotBe(SessionStatus.Active);
    }

    [Test]
    public async Task SessionEnded_FiresSessionEndedEvent()
    {
        _store.RegisterTestAgent();
        _store.StartSession(TestData.MachineId, TestData.SessionId, TestData.WorkingDir, null);
        var firedExitCode = -1;
        _notifier.SessionEnded += (_, _, code) => firedExitCode = code;

        await _hub.SessionEnded(TestData.MachineId, TestData.SessionId, 42);

        firedExitCode.Should().Be(42);
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Heartbeat_FiresHeartbeatReceivedEvent()
    {
        _store.RegisterTestAgent();
        string? receivedId = null;
        _notifier.HeartbeatReceived += id => receivedId = id;

        await _hub.Heartbeat(TestData.MachineId);

        receivedId.Should().Be(TestData.MachineId);
    }

    [Test]
    public async Task Heartbeat_UpdatesLastHeartbeatAt()
    {
        _store.RegisterTestAgent();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await _hub.Heartbeat(TestData.MachineId);

        _store.GetAllMachines()
              .Single(m => m.MachineId == TestData.MachineId)
              .LastHeartbeatAt.Should().BeOnOrAfter(before);
    }

    // ── OnDisconnectedAsync ───────────────────────────────────────────────────

    [Test]
    public async Task OnDisconnectedAsync_WithMachineIdInContext_MarksAgentOffline()
    {
        await _hub.RegisterAgent(MakeRegistration());
        // MachineId was written to Items by RegisterAgent

        await _hub.OnDisconnectedAsync(null);

        _store.GetConnectionId(TestData.MachineId).Should().BeNull();
    }

    [Test]
    public async Task OnDisconnectedAsync_WithMachineIdInContext_FiresAgentDisconnectedEvent()
    {
        await _hub.RegisterAgent(MakeRegistration());
        string? disconnectedId = null;
        _notifier.AgentDisconnected += id => disconnectedId = id;

        await _hub.OnDisconnectedAsync(null);

        disconnectedId.Should().Be(TestData.MachineId);
    }

    [Test]
    public async Task OnDisconnectedAsync_WithoutMachineIdInContext_DoesNotThrow()
    {
        // Context.Items is empty — no MachineId has been stored (agent never registered)
        await _hub.Invoking(h => h.OnDisconnectedAsync(null))
            .Should().NotThrowAsync();
    }

    [Test]
    public async Task OnDisconnectedAsync_WithoutMachineIdInContext_DoesNotFireDisconnectEvent()
    {
        var fired = false;
        _notifier.AgentDisconnected += _ => fired = true;

        await _hub.OnDisconnectedAsync(null);

        fired.Should().BeFalse();
    }

    // ── OnConnectedAsync ──────────────────────────────────────────────────────

    [Test]
    public async Task OnConnectedAsync_DoesNotThrow()
    {
        await _hub.Invoking(h => h.OnConnectedAsync())
            .Should().NotThrowAsync();
    }
}
