using System.Collections.Concurrent;
using ClaudeManager.Agent.Models;
using ClaudeManager.Shared.Dto;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Agent;

public class AgentService : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly string _machineId;
    private readonly string _binary;
    private readonly ILogger<AgentService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private HubConnection _hub = default!;

    // Active processes keyed by sessionId
    private readonly ConcurrentDictionary<string, ClaudeProcess> _processes = new();

    // Pending prompts queued while a process is already running for a session
    private readonly ConcurrentDictionary<string, Queue<string>> _pendingPrompts = new();

    public AgentService(
        AgentConfig config,
        string machineId,
        string binary,
        ILogger<AgentService> logger,
        ILoggerFactory loggerFactory)
    {
        _config       = config;
        _machineId    = machineId;
        _binary       = binary;
        _logger       = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _hub = BuildConnection();
        RegisterHandlers();

        await ConnectWithRetry(ct);
        await Task.Delay(Timeout.Infinite, ct);
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private HubConnection BuildConnection() =>
        new HubConnectionBuilder()
            .WithUrl(_config.HubUrl.TrimEnd('/') + "/agenthub", opts =>
            {
                opts.Headers["X-Agent-Secret"] = _config.SharedSecret;
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .Build();

    private void RegisterHandlers()
    {
        _hub.On<StartSessionRequest>("StartSession",  OnStartSession);
        _hub.On<SendPromptRequest>("SendPrompt",      OnSendPrompt);
        _hub.On<KillSessionRequest>("KillSession",    OnKillSession);

        _hub.Reconnected += async connectionId =>
        {
            _logger.LogInformation("Reconnected to hub (connectionId={Id})", connectionId);
            await RegisterWithHub();
        };

        _hub.Closed += ex =>
        {
            _logger.LogWarning(ex, "Hub connection closed");
            return Task.CompletedTask;
        };
    }

    private async Task ConnectWithRetry(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hub.StartAsync(ct);
                _logger.LogInformation("Connected to hub at {Url}", _config.HubUrl);
                await RegisterWithHub();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to connect to hub, retrying in 5s...");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task RegisterWithHub()
    {
        var registration = new AgentRegistration(
            MachineId:    _machineId,
            DisplayName:  _config.DisplayName ?? Environment.MachineName,
            Platform:     GetPlatform(),
            AgentVersion: typeof(AgentService).Assembly.GetName().Version?.ToString() ?? "0.0.0");

        await _hub.InvokeAsync("RegisterAgent", registration);
    }

    // ── Hub command handlers ──────────────────────────────────────────────────

    private async Task OnStartSession(StartSessionRequest req)
    {
        if (!Directory.Exists(req.WorkingDirectory))
        {
            _logger.LogWarning("StartSession: working directory does not exist: {Dir}", req.WorkingDirectory);
            return;
        }

        var sessionId = req.ResumeSessionId ?? $"pending-{Guid.NewGuid()}";
        _logger.LogInformation("Starting session {SessionId} in {Dir}", sessionId, req.WorkingDirectory);

        await SpawnProcessAsync(sessionId, req.WorkingDirectory, req.InitialPrompt, req.ResumeSessionId);
    }

    private async Task OnSendPrompt(SendPromptRequest req)
    {
        if (_processes.TryGetValue(req.SessionId, out var running) && running.IsRunning)
        {
            // Queue the prompt for after the current process exits
            _pendingPrompts.GetOrAdd(req.SessionId, _ => new Queue<string>()).Enqueue(req.Prompt);
            _logger.LogInformation("Queued prompt for session {SessionId} (process still running)", req.SessionId);
            return;
        }

        var session = _processes.ContainsKey(req.SessionId) ? req.SessionId : null;
        if (session is null && !req.SessionId.StartsWith("pending-"))
            session = req.SessionId;

        await SpawnProcessAsync(req.SessionId, GetWorkingDir(req.SessionId), req.Prompt, req.SessionId);
    }

    private async Task OnKillSession(KillSessionRequest req)
    {
        if (_processes.TryRemove(req.SessionId, out var proc))
        {
            _logger.LogInformation("Killing session {SessionId}", req.SessionId);
            await proc.KillAsync();
            await proc.DisposeAsync();
        }
    }

    // ── Process management ────────────────────────────────────────────────────

    private async Task SpawnProcessAsync(string sessionId, string workingDir, string prompt, string? resumeId)
    {
        var proc = new ClaudeProcess(
            _binary,
            workingDir,
            prompt,
            resumeId,
            _loggerFactory.CreateLogger<ClaudeProcess>());

        proc.OnOutputLine  += line => ForwardLine(line, proc.SessionId ?? sessionId);
        proc.OnStderrLine  += line => ForwardStderr(line, proc.SessionId ?? sessionId);
        proc.OnExit        += exitCode => OnProcessExited(proc, sessionId, workingDir, exitCode);

        _processes[sessionId] = proc;

        // Announce session start to hub
        await _hub.InvokeAsync("SessionStarted", _machineId, resumeId ?? sessionId, workingDir, prompt);

        await proc.StartAsync(default);
    }

    private async Task ForwardLine(string rawJson, string sessionId)
    {
        try
        {
            await _hub.InvokeAsync("StreamLine", new StreamLineDto(
                MachineId: _machineId,
                SessionId: sessionId,
                RawJson:   rawJson,
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to forward line to hub for session {SessionId}", sessionId);
        }
    }

    private async Task ForwardStderr(string line, string sessionId)
    {
        // Wrap stderr as a JSON-like envelope so AgentHub can detect it
        var escaped = line.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var json    = $"{{\"stderr\":\"{escaped}\"}}";
        await ForwardLine(json, sessionId);
    }

    private async Task OnProcessExited(ClaudeProcess proc, string pendingSessionId, string workingDir, int exitCode)
    {
        // The real session ID from claude (may differ from our initial pending- ID)
        var realSessionId = proc.SessionId ?? pendingSessionId;

        // Swap the process registration to the real session ID
        _processes.TryRemove(pendingSessionId, out _);
        _processes.TryRemove(realSessionId, out _);
        await proc.DisposeAsync();

        await _hub.InvokeAsync("SessionEnded", _machineId, realSessionId, exitCode);

        // Drain any queued prompts
        if (_pendingPrompts.TryRemove(realSessionId, out var queue) ||
            _pendingPrompts.TryRemove(pendingSessionId, out queue))
        {
            if (queue.TryDequeue(out var nextPrompt))
            {
                _logger.LogInformation("Dispatching queued prompt for session {SessionId}", realSessionId);
                await SpawnProcessAsync(realSessionId, workingDir, nextPrompt, realSessionId);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetWorkingDir(string sessionId)
    {
        // Best-effort: use config default (agent doesn't store session→dir mapping beyond processes dict)
        return _config.DefaultWorkingDirectory;
    }

    private static string GetPlatform()
    {
        if (OperatingSystem.IsWindows()) return "win32";
        if (OperatingSystem.IsMacOS())  return "darwin";
        return "linux";
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        foreach (var proc in _processes.Values)
        {
            try { await proc.KillAsync(); await proc.DisposeAsync(); } catch { /* ignore on shutdown */ }
        }
        await _hub.DisposeAsync();
        await base.StopAsync(ct);
    }
}

/// <summary>
/// Retries indefinitely with capped backoff: 0, 2, 5, 15, 30s, then every 60s.
/// </summary>
internal sealed class InfiniteRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] InitialDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
         TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        retryContext.PreviousRetryCount < InitialDelays.Length
            ? InitialDelays[retryContext.PreviousRetryCount]
            : TimeSpan.FromSeconds(60);
}
