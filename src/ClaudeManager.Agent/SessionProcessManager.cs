using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Agent;

/// <summary>
/// Manages the lifecycle of claude processes for a single agent.
/// Handles process spawning, prompt queuing, and session-ID reconciliation.
/// Has no SignalR dependency — all output and lifecycle events are surfaced
/// via callbacks so AgentService can forward them to the hub.
/// </summary>
public class SessionProcessManager
{
    private readonly IClaudeProcessFactory _factory;
    private readonly ILogger<SessionProcessManager> _logger;

    private readonly ConcurrentDictionary<string, IClaudeProcess> _processes    = new();
    private readonly ConcurrentDictionary<string, Queue<string>>  _pendingPrompts = new();
    // Tracks the working directory per session for use when draining queued prompts
    private readonly ConcurrentDictionary<string, string> _workingDirs = new();

    // Callbacks set by AgentService — called on output lines and session lifecycle events
    public Func<string, string, Task>? OnOutputLine  { get; set; } // (sessionId, rawJson)
    public Func<string, string, Task>? OnStderrLine  { get; set; } // (sessionId, line)
    public Func<string, int,    Task>? OnSessionEnded { get; set; } // (sessionId, exitCode)

    public SessionProcessManager(IClaudeProcessFactory factory, ILogger<SessionProcessManager> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    // ── Public API (called by AgentService hub handlers) ─────────────────────

    public async Task StartSessionAsync(
        string sessionId, string workingDirectory, string prompt, string? resumeId,
        string? systemContext = null)
    {
        _workingDirs[sessionId] = workingDirectory;
        var combinedPrompt = systemContext is not null
            ? $"{systemContext}\n\n{prompt}"
            : prompt;
        await SpawnAsync(sessionId, workingDirectory, combinedPrompt, resumeId);
    }

    public async Task SendPromptAsync(string sessionId, string workingDirectory, string prompt)
    {
        _workingDirs[sessionId] = workingDirectory;

        if (_processes.TryGetValue(sessionId, out var running) && running.IsRunning)
        {
            _pendingPrompts.GetOrAdd(sessionId, _ => new Queue<string>()).Enqueue(prompt);
            _logger.LogInformation("Queued prompt for session {SessionId} (process still running)", sessionId);
            return;
        }

        await SpawnAsync(sessionId, workingDirectory, prompt, resumeId: sessionId);
    }

    public async Task KillSessionAsync(string sessionId)
    {
        if (_processes.TryRemove(sessionId, out var proc))
        {
            _logger.LogInformation("Killing session {SessionId}", sessionId);
            await proc.KillAsync();
            await proc.DisposeAsync();
        }
    }

    public async Task KillAllAsync()
    {
        foreach (var proc in _processes.Values)
        {
            try { await proc.KillAsync(); await proc.DisposeAsync(); } catch { /* ignore on shutdown */ }
        }
        _processes.Clear();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task SpawnAsync(string sessionId, string workingDir, string prompt, string? resumeId)
    {
        var proc = _factory.Create(workingDir, prompt, resumeId);

        proc.OnOutputLine += line  => HandleOutputLine(proc, sessionId, line);
        proc.OnStderrLine += line  => HandleStderrLine(proc, sessionId, line);
        proc.OnExit       += code  => HandleExit(proc, sessionId, workingDir, code);

        _processes[sessionId] = proc;
        await proc.StartAsync(CancellationToken.None);
    }

    private async Task HandleOutputLine(IClaudeProcess proc, string pendingSessionId, string rawJson)
    {
        var sessionId = proc.SessionId ?? pendingSessionId;
        if (OnOutputLine is not null)
            await OnOutputLine(sessionId, rawJson);
    }

    private async Task HandleStderrLine(IClaudeProcess proc, string pendingSessionId, string line)
    {
        var sessionId = proc.SessionId ?? pendingSessionId;
        if (OnStderrLine is not null)
            await OnStderrLine(sessionId, line);
    }

    private async Task HandleExit(IClaudeProcess proc, string pendingSessionId, string workingDir, int exitCode)
    {
        var realSessionId = proc.SessionId ?? pendingSessionId;

        _processes.TryRemove(pendingSessionId, out _);
        _processes.TryRemove(realSessionId, out _);
        await proc.DisposeAsync();

        if (OnSessionEnded is not null)
            await OnSessionEnded(realSessionId, exitCode);

        // Drain one queued prompt (if any) now that the session is free
        if (_pendingPrompts.TryRemove(realSessionId,   out var queue) ||
            _pendingPrompts.TryRemove(pendingSessionId, out queue))
        {
            if (queue.TryDequeue(out var nextPrompt))
            {
                _logger.LogInformation("Dispatching queued prompt for session {SessionId}", realSessionId);
                var dir = _workingDirs.GetValueOrDefault(realSessionId, workingDir);
                await SpawnAsync(realSessionId, dir, nextPrompt, resumeId: realSessionId);
            }
        }
    }
}
