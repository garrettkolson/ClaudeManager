using ClaudeManager.Hub.Hubs;
using ClaudeManager.Hub.Services;
using ClaudeManager.Hub.Tests.Helpers;
using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
// ISingleClientProxy is the type returned by IHubClients.Client() in SignalR 10

namespace ClaudeManager.Hub.Tests.Services;

[TestFixture]
public class AgentCommandServiceTests
{
    private Mock<IHubContext<AgentHub>> _hubCtxMock = default!;
    private Mock<ISingleClientProxy> _proxyMock = default!;
    private SessionStore _store = default!;
    private AgentCommandService _svc = default!;

    [SetUp]
    public void SetUp()
    {
        (_hubCtxMock, _proxyMock) = HubContextFactory.CreateMock();

        var (store, _) = new SessionStoreBuilder().Build();
        _store = store;
        _store.RegisterTestAgent();

        _svc = new AgentCommandService(_hubCtxMock.Object, _store, NullLogger<AgentCommandService>.Instance);
    }

    private StartSessionRequest MakeStartRequest() =>
        new(TestData.MachineId, TestData.WorkingDir, "hello", null);

    private SendPromptRequest MakeSendRequest() =>
        new(TestData.MachineId, TestData.SessionId, "do it");

    // ── StartSessionAsync ────────────────────────────────────────────────────

    [Test]
    public async Task StartSessionAsync_OnlineMachine_ReturnsTrue()
    {
        var result = await _svc.StartSessionAsync(MakeStartRequest());
        result.Should().BeTrue();
    }

    [Test]
    public async Task StartSessionAsync_OnlineMachine_SendsStartSessionToClient()
    {
        await _svc.StartSessionAsync(MakeStartRequest());

        _proxyMock.Verify(p => p.SendCoreAsync(
            "StartSession",
            It.Is<object[]>(args => args.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task StartSessionAsync_OfflineMachine_ReturnsFalse()
    {
        _store.MarkAgentDisconnected(TestData.MachineId);
        var result = await _svc.StartSessionAsync(MakeStartRequest());
        result.Should().BeFalse();
    }

    [Test]
    public async Task StartSessionAsync_OfflineMachine_DoesNotCallSendAsync()
    {
        _store.MarkAgentDisconnected(TestData.MachineId);
        await _svc.StartSessionAsync(MakeStartRequest());

        _proxyMock.Verify(p => p.SendCoreAsync(
            It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task StartSessionAsync_UnknownMachine_ReturnsFalse()
    {
        var result = await _svc.StartSessionAsync(
            new StartSessionRequest("unknown-machine", TestData.WorkingDir, "hi", null));
        result.Should().BeFalse();
    }

    // ── SendPromptAsync ───────────────────────────────────────────────────────

    [Test]
    public async Task SendPromptAsync_OnlineMachine_ReturnsTrue()
    {
        var result = await _svc.SendPromptAsync(MakeSendRequest());
        result.Should().BeTrue();
    }

    [Test]
    public async Task SendPromptAsync_OnlineMachine_SendsSendPromptToClient()
    {
        await _svc.SendPromptAsync(MakeSendRequest());

        _proxyMock.Verify(p => p.SendCoreAsync(
            "SendPrompt",
            It.Is<object[]>(args => args.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SendPromptAsync_OfflineMachine_ReturnsFalse()
    {
        _store.MarkAgentDisconnected(TestData.MachineId);
        var result = await _svc.SendPromptAsync(MakeSendRequest());
        result.Should().BeFalse();
    }

    // ── KillSessionAsync ──────────────────────────────────────────────────────

    [Test]
    public async Task KillSessionAsync_OnlineMachine_ReturnsTrue()
    {
        var result = await _svc.KillSessionAsync(TestData.MachineId, TestData.SessionId);
        result.Should().BeTrue();
    }

    [Test]
    public async Task KillSessionAsync_OnlineMachine_SendsKillSessionToClient()
    {
        await _svc.KillSessionAsync(TestData.MachineId, TestData.SessionId);

        _proxyMock.Verify(p => p.SendCoreAsync(
            "KillSession",
            It.Is<object[]>(args => args.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task KillSessionAsync_OfflineMachine_ReturnsFalse()
    {
        _store.MarkAgentDisconnected(TestData.MachineId);
        var result = await _svc.KillSessionAsync(TestData.MachineId, TestData.SessionId);
        result.Should().BeFalse();
    }
}
