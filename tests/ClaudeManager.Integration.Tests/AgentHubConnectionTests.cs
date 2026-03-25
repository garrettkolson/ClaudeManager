using ClaudeManager.Shared.Dto;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClaudeManager.Integration.Tests;

/// <summary>
/// Integration tests that spin up the real Hub on an in-process TestServer and connect
/// a raw SignalR <see cref="HubConnection"/> (simulating the agent protocol) to exercise
/// the full Hub-side flow without running the real agent or claude binary.
/// </summary>
[TestFixture]
public class AgentHubConnectionTests
{
    private HubWebApplicationFactory _factory = default!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new HubWebApplicationFactory();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentRegistration Reg(string machineId) =>
        new(machineId, $"Test Machine {machineId[..8]}", "linux", "1.0.0");

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    // ── Authentication ────────────────────────────────────────────────────────

    [Test]
    public async Task Connect_WithWrongSecret_StartAsyncThrows()
    {
        await using var conn = _factory.CreateAgentConnection(secret: "wrong-secret");
        await conn.Awaiting(c => c.StartAsync()).Should().ThrowAsync<Exception>();
    }

    [Test]
    public async Task Connect_WithoutSecret_StartAsyncThrows()
    {
        await using var conn = _factory.CreateAgentConnection(secret: null);
        await conn.Awaiting(c => c.StartAsync()).Should().ThrowAsync<Exception>();
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [Test]
    public async Task RegisterAgent_NewMachine_AppearsOnlineInStore()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        await using var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();

        await conn.InvokeAsync("RegisterAgent", Reg(machineId));

        var machine = _factory.SessionStore.GetMachine(machineId);
        machine.Should().NotBeNull();
        machine!.IsOnline.Should().BeTrue();
        machine.MachineId.Should().Be(machineId);
    }

    [Test]
    public async Task RegisterAgent_SameMachineReconnects_ConnectionIdUpdated()
    {
        var machineId = $"m-{Guid.NewGuid():N}";

        await using var conn1 = _factory.CreateAgentConnection();
        await conn1.StartAsync();
        await conn1.InvokeAsync("RegisterAgent", Reg(machineId));
        var firstConnId = _factory.SessionStore.GetMachine(machineId)!.SignalRConnectionId;

        await using var conn2 = _factory.CreateAgentConnection();
        await conn2.StartAsync();
        await conn2.InvokeAsync("RegisterAgent", Reg(machineId));

        var machine = _factory.SessionStore.GetMachine(machineId);
        machine!.IsOnline.Should().BeTrue();
        machine.SignalRConnectionId.Should().NotBe(firstConnId);
    }

    // ── Disconnect ────────────────────────────────────────────────────────────

    [Test]
    public async Task Disconnect_AfterRegistration_MachineMarkedOffline()
    {
        var machineId = $"m-{Guid.NewGuid():N}";

        var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));

        await conn.StopAsync();
        await conn.DisposeAsync();

        await WaitForAsync(() => _factory.SessionStore.GetMachine(machineId)?.IsOnline == false);

        _factory.SessionStore.GetMachine(machineId)!.IsOnline.Should().BeFalse();
    }

    [Test]
    public async Task Disconnect_WithActiveSession_SessionMarkedDisconnected()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));
        await conn.InvokeAsync("SessionStarted", machineId, sessionId, Path.GetTempPath(), "hi");

        await conn.StopAsync();
        await conn.DisposeAsync();

        await WaitForAsync(() =>
            _factory.SessionStore.GetSession(machineId, sessionId)?.Status == SessionStatus.Disconnected);

        _factory.SessionStore.GetSession(machineId, sessionId)!.Status
            .Should().Be(SessionStatus.Disconnected);
    }

    // ── Hub → Agent commands ──────────────────────────────────────────────────

    [Test]
    public async Task StartSessionCommand_DeliveredToRegisteredAgent()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<StartSessionRequest>();

        await using var conn = _factory.CreateAgentConnection();
        conn.On<StartSessionRequest>("StartSession", req => tcs.TrySetResult(req));

        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));

        var sent = await _factory.CommandService.StartSessionAsync(
            new StartSessionRequest(machineId, Path.GetTempPath(), "test prompt", null));

        sent.Should().BeTrue();

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.MachineId.Should().Be(machineId);
        received.InitialPrompt.Should().Be("test prompt");
    }

    [Test]
    public async Task SendPromptCommand_DeliveredToRegisteredAgent()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<SendPromptRequest>();

        await using var conn = _factory.CreateAgentConnection();
        conn.On<SendPromptRequest>("SendPrompt", req => tcs.TrySetResult(req));

        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));

        var sent = await _factory.CommandService.SendPromptAsync(
            new SendPromptRequest(machineId, sessionId, "follow-up"));

        sent.Should().BeTrue();

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.SessionId.Should().Be(sessionId);
        received.Prompt.Should().Be("follow-up");
    }

    [Test]
    public async Task KillSessionCommand_DeliveredToRegisteredAgent()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<KillSessionRequest>();

        await using var conn = _factory.CreateAgentConnection();
        conn.On<KillSessionRequest>("KillSession", req => tcs.TrySetResult(req));

        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));

        var sent = await _factory.CommandService.KillSessionAsync(machineId, sessionId);

        sent.Should().BeTrue();

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.MachineId.Should().Be(machineId);
        received.SessionId.Should().Be(sessionId);
    }

    // ── Agent → Hub messages ──────────────────────────────────────────────────

    [Test]
    public async Task StreamLine_LineStoredInSessionStore()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        await using var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));
        await conn.InvokeAsync("SessionStarted", machineId, sessionId, Path.GetTempPath(), "hello");

        await conn.InvokeAsync("StreamLine", new StreamLineDto(
            machineId, sessionId,
            "{\"type\":\"assistant\",\"text\":\"hi\"}",
            DateTimeOffset.UtcNow));

        await WaitForAsync(() =>
            (_factory.SessionStore.GetSession(machineId, sessionId)?.OutputLines.Count ?? 0) >= 1);

        var session = _factory.SessionStore.GetSession(machineId, sessionId)!;
        session.OutputLines.Should().HaveCount(1);
        session.OutputLines[0].Kind.Should().Be(StreamLineKind.AssistantToken);
    }

    [Test]
    public async Task SessionEnded_SessionStatusSetToEnded()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        await using var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));
        await conn.InvokeAsync("SessionStarted", machineId, sessionId, Path.GetTempPath(), "hello");
        await conn.InvokeAsync("SessionEnded",   machineId, sessionId, 0);

        await WaitForAsync(() =>
            _factory.SessionStore.GetSession(machineId, sessionId)?.Status == SessionStatus.Ended);

        var session = _factory.SessionStore.GetSession(machineId, sessionId)!;
        session.Status.Should().Be(SessionStatus.Ended);
        session.EndedAt.Should().NotBeNull();
        session.IsProcessRunning.Should().BeFalse();
    }

    [Test]
    public async Task Heartbeat_UpdatesLastHeartbeatAt()
    {
        var machineId = $"m-{Guid.NewGuid():N}";

        await using var conn = _factory.CreateAgentConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));

        var before = _factory.SessionStore.GetMachine(machineId)!.LastHeartbeatAt;
        await Task.Delay(15); // ensure clock advances

        await conn.InvokeAsync("Heartbeat", machineId);

        await WaitForAsync(() =>
            _factory.SessionStore.GetMachine(machineId)!.LastHeartbeatAt > before);

        _factory.SessionStore.GetMachine(machineId)!.LastHeartbeatAt.Should().BeAfter(before);
    }

    // ── Full round-trip ───────────────────────────────────────────────────────

    [Test]
    public async Task FullSessionLifecycle_StartStreamEnd_AllStateCorrect()
    {
        var machineId = $"m-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";
        var startTcs  = new TaskCompletionSource<StartSessionRequest>();

        await using var conn = _factory.CreateAgentConnection();
        conn.On<StartSessionRequest>("StartSession", req => startTcs.TrySetResult(req));

        await conn.StartAsync();
        await conn.InvokeAsync("RegisterAgent", Reg(machineId));

        // Hub sends StartSession to the agent
        await _factory.CommandService.StartSessionAsync(
            new StartSessionRequest(machineId, Path.GetTempPath(), "say hello", null));

        // Agent receives the command
        var cmd = await startTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cmd.InitialPrompt.Should().Be("say hello");

        // Agent reports: started → 3 output lines → ended
        await conn.InvokeAsync("SessionStarted", machineId, sessionId, Path.GetTempPath(), "say hello");

        await conn.InvokeAsync("StreamLine", new StreamLineDto(
            machineId, sessionId, "{\"type\":\"assistant\",\"text\":\"Hello!\"}", DateTimeOffset.UtcNow));
        await conn.InvokeAsync("StreamLine", new StreamLineDto(
            machineId, sessionId, "{\"type\":\"tool_use\",\"name\":\"Read\"}", DateTimeOffset.UtcNow));
        await conn.InvokeAsync("StreamLine", new StreamLineDto(
            machineId, sessionId, "{\"type\":\"result\",\"subtype\":\"success\"}", DateTimeOffset.UtcNow));

        await conn.InvokeAsync("SessionEnded", machineId, sessionId, 0);

        await WaitForAsync(() =>
        {
            var s = _factory.SessionStore.GetSession(machineId, sessionId);
            return s?.Status == SessionStatus.Ended && s.OutputLines.Count == 3;
        });

        var session = _factory.SessionStore.GetSession(machineId, sessionId)!;
        session.Status.Should().Be(SessionStatus.Ended);
        session.OutputLines.Should().HaveCount(3);
        session.OutputLines[0].Kind.Should().Be(StreamLineKind.AssistantToken);
        session.OutputLines[1].Kind.Should().Be(StreamLineKind.ToolUse);
        session.OutputLines[2].Kind.Should().Be(StreamLineKind.ResultSummary);
        session.EndedAt.Should().NotBeNull();
        _factory.SessionStore.GetMachine(machineId)!.IsOnline.Should().BeTrue();
    }
}
