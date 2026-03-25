using ClaudeManager.Agent.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeManager.Agent.Tests;

[TestFixture]
public class SessionProcessManagerTests
{
    private FakeClaudeProcessFactory _factory = default!;
    private SessionProcessManager _manager = default!;

    [SetUp]
    public void SetUp()
    {
        _factory = new FakeClaudeProcessFactory();
        _manager = new SessionProcessManager(_factory, NullLogger<SessionProcessManager>.Instance);
    }

    // ── StartSessionAsync ────────────────────────────────────────────────────

    [Test]
    public async Task StartSessionAsync_CreatesProcessAndStartsIt()
    {
        await _manager.StartSessionAsync("sess-1", "/tmp", "hello", null);

        _factory.LastCreated.Should().NotBeNull();
        _factory.LastCreated!.StartWasCalled.Should().BeTrue();
    }

    [Test]
    public async Task StartSessionAsync_StoresWorkingDirectoryForLaterUse()
    {
        // Start a session, let it exit, verify queued prompt uses stored dir
        var firstProc = new FakeClaudeProcess();
        var secondProc = new FakeClaudeProcess();
        _factory.Enqueue(firstProc);
        _factory.Enqueue(secondProc);

        await _manager.StartSessionAsync("sess-1", "/stored-dir", "first", null);
        _manager.OnOutputLine = (_, _) => Task.CompletedTask;
        _manager.OnSessionEnded = (_, _) => Task.CompletedTask;

        // Queue a prompt then let the process exit; second spawn should use stored dir
        await _manager.SendPromptAsync("sess-1", "/different-dir", "second"); // queued
        await firstProc.SimulateExit(0); // drains queue

        _factory.Created.Should().HaveCount(2);
    }

    // ── SendPromptAsync ───────────────────────────────────────────────────────

    [Test]
    public async Task SendPromptAsync_ProcessNotRunning_SpawnsNewProcess()
    {
        await _manager.SendPromptAsync("sess-1", "/tmp", "new prompt");

        _factory.LastCreated.Should().NotBeNull();
        _factory.LastCreated!.StartWasCalled.Should().BeTrue();
    }

    [Test]
    public async Task SendPromptAsync_ProcessAlreadyRunning_QueuesPrompt()
    {
        var proc = new FakeClaudeProcess();
        _factory.Enqueue(proc);

        await _manager.StartSessionAsync("sess-1", "/tmp", "first", null);
        // proc is IsRunning = true at this point

        await _manager.SendPromptAsync("sess-1", "/tmp", "second");

        // Only one process was created (the second prompt was queued)
        _factory.Created.Should().HaveCount(1);
    }

    [Test]
    public async Task SendPromptAsync_ProcessAlreadyRunning_DoesNotSpawnNewProcess()
    {
        var proc = new FakeClaudeProcess();
        _factory.Enqueue(proc);
        await _manager.StartSessionAsync("sess-1", "/tmp", "first", null);

        var secondProc = new FakeClaudeProcess();
        _factory.Enqueue(secondProc);
        await _manager.SendPromptAsync("sess-1", "/tmp", "queued");

        secondProc.StartWasCalled.Should().BeFalse();
    }

    // ── KillSessionAsync ──────────────────────────────────────────────────────

    [Test]
    public async Task KillSessionAsync_RunningProcess_KillsAndRemoves()
    {
        var proc = new FakeClaudeProcess();
        _factory.Enqueue(proc);
        await _manager.StartSessionAsync("sess-1", "/tmp", "hello", null);

        await _manager.KillSessionAsync("sess-1");

        proc.KillWasCalled.Should().BeTrue();
    }

    [Test]
    public async Task KillSessionAsync_UnknownSession_DoesNotThrow()
    {
        await FluentActions.Awaiting(() => _manager.KillSessionAsync("nonexistent"))
            .Should().NotThrowAsync();
    }

    // ── Process exit and queue draining ───────────────────────────────────────

    [Test]
    public async Task OnProcessExited_PendingQueueHasItem_DequeuesAndSpawnsNext()
    {
        var firstProc  = new FakeClaudeProcess();
        var secondProc = new FakeClaudeProcess();
        _factory.Enqueue(firstProc);
        _factory.Enqueue(secondProc);

        _manager.OnOutputLine   = (_, _) => Task.CompletedTask;
        _manager.OnStderrLine   = (_, _) => Task.CompletedTask;
        _manager.OnSessionEnded = (_, _) => Task.CompletedTask;

        await _manager.StartSessionAsync("sess-1", "/tmp", "first", null);
        await _manager.SendPromptAsync("sess-1", "/tmp", "second"); // queued

        await firstProc.SimulateExit(0); // should drain queue and spawn secondProc

        secondProc.StartWasCalled.Should().BeTrue();
    }

    [Test]
    public async Task OnProcessExited_PendingQueueEmpty_DoesNotSpawnNext()
    {
        var proc = new FakeClaudeProcess();
        _factory.Enqueue(proc);

        _manager.OnOutputLine   = (_, _) => Task.CompletedTask;
        _manager.OnStderrLine   = (_, _) => Task.CompletedTask;
        _manager.OnSessionEnded = (_, _) => Task.CompletedTask;

        await _manager.StartSessionAsync("sess-1", "/tmp", "only prompt", null);
        await proc.SimulateExit(0);

        _factory.Created.Should().HaveCount(1); // no second spawn
    }

    // ── Callbacks ─────────────────────────────────────────────────────────────

    [Test]
    public async Task OnOutputLine_ForwardedToCallback()
    {
        var proc = new FakeClaudeProcess();
        proc.SessionId = "sess-1";
        _factory.Enqueue(proc);

        string? receivedSession = null;
        string? receivedJson = null;
        _manager.OnOutputLine = (sid, json) => { receivedSession = sid; receivedJson = json; return Task.CompletedTask; };

        await _manager.StartSessionAsync("sess-1", "/tmp", "hello", null);
        await proc.SimulateOutputLine("{\"type\":\"assistant\"}");

        receivedSession.Should().Be("sess-1");
        receivedJson.Should().Be("{\"type\":\"assistant\"}");
    }

    [Test]
    public async Task OnStderrLine_ForwardedToCallback()
    {
        var proc = new FakeClaudeProcess();
        proc.SessionId = "sess-1";
        _factory.Enqueue(proc);

        string? receivedLine = null;
        _manager.OnStderrLine = (_, line) => { receivedLine = line; return Task.CompletedTask; };

        await _manager.StartSessionAsync("sess-1", "/tmp", "hello", null);
        await proc.SimulateStderrLine("error output");

        receivedLine.Should().Be("error output");
    }

    [Test]
    public async Task OnSessionEnded_ForwardedToCallback()
    {
        var proc = new FakeClaudeProcess();
        proc.SessionId = "sess-1";
        _factory.Enqueue(proc);

        int? receivedCode = null;
        _manager.OnOutputLine   = (_, _) => Task.CompletedTask;
        _manager.OnStderrLine   = (_, _) => Task.CompletedTask;
        _manager.OnSessionEnded = (_, code) => { receivedCode = code; return Task.CompletedTask; };

        await _manager.StartSessionAsync("sess-1", "/tmp", "hello", null);
        await proc.SimulateExit(42);

        receivedCode.Should().Be(42);
    }

    [Test]
    public async Task OnSessionEnded_UsesRealSessionIdNotPendingId()
    {
        var proc = new FakeClaudeProcess { SessionId = "real-session-id" };
        _factory.Enqueue(proc);

        string? reportedId = null;
        _manager.OnOutputLine   = (_, _) => Task.CompletedTask;
        _manager.OnStderrLine   = (_, _) => Task.CompletedTask;
        _manager.OnSessionEnded = (sid, _) => { reportedId = sid; return Task.CompletedTask; };

        await _manager.StartSessionAsync("pending-session-id", "/tmp", "hello", null);
        await proc.SimulateExit(0);

        reportedId.Should().Be("real-session-id");
    }

    // ── KillAllAsync ──────────────────────────────────────────────────────────

    [Test]
    public async Task KillAllAsync_KillsAllActiveProcesses()
    {
        var proc1 = new FakeClaudeProcess();
        var proc2 = new FakeClaudeProcess();
        _factory.Enqueue(proc1);
        _factory.Enqueue(proc2);

        await _manager.StartSessionAsync("sess-1", "/tmp", "a", null);
        await _manager.StartSessionAsync("sess-2", "/tmp", "b", null);

        await _manager.KillAllAsync();

        proc1.KillWasCalled.Should().BeTrue();
        proc2.KillWasCalled.Should().BeTrue();
    }
}
