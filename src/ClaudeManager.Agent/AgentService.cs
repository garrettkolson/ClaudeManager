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
    private readonly SessionProcessManager _processManager;
    private readonly ILogger<AgentService> _logger;

    private HubConnection _hub = default!;

    public AgentService(
        AgentConfig config,
        string machineId,
        SessionProcessManager processManager,
        ILogger<AgentService> logger)
    {
        _config         = config;
        _machineId      = machineId;
        _processManager = processManager;
        _logger         = logger;

        // Wire process manager callbacks to hub forwarding
        _processManager.OnOutputLine   = ForwardOutputLine;
        _processManager.OnStderrLine   = ForwardStderrLine;
        _processManager.OnSessionEnded = ForwardSessionEnded;
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
        _hub.On<StartSessionRequest>("StartSession", OnStartSession);
        _hub.On<SendPromptRequest>("SendPrompt",     OnSendPrompt);
        _hub.On<KillSessionRequest>("KillSession",   OnKillSession);

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

        await _hub.InvokeAsync("SessionStarted", _machineId, sessionId, req.WorkingDirectory, req.InitialPrompt);
        await _processManager.StartSessionAsync(sessionId, req.WorkingDirectory, req.InitialPrompt, req.ResumeSessionId, req.SystemContext);
    }

    private Task OnSendPrompt(SendPromptRequest req) =>
        _processManager.SendPromptAsync(req.SessionId, _config.DefaultWorkingDirectory, req.Prompt);

    private Task OnKillSession(KillSessionRequest req) =>
        _processManager.KillSessionAsync(req.SessionId);

    // ── Process manager callbacks → hub ──────────────────────────────────────

    private async Task ForwardOutputLine(string sessionId, string rawJson)
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

    private async Task ForwardStderrLine(string sessionId, string line)
    {
        var escaped = line.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var json    = $"{{\"stderr\":\"{escaped}\"}}";
        await ForwardOutputLine(sessionId, json);
    }

    private async Task ForwardSessionEnded(string sessionId, int exitCode)
    {
        try
        {
            await _hub.InvokeAsync("SessionEnded", _machineId, sessionId, exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify hub of session end for {SessionId}", sessionId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetPlatform()
    {
        if (OperatingSystem.IsWindows()) return "win32";
        if (OperatingSystem.IsMacOS())  return "darwin";
        return "linux";
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await _processManager.KillAllAsync();
        await _hub.DisposeAsync();
        await base.StopAsync(ct);
    }
}

/// <summary>
/// Retries indefinitely with capped backoff: 0, 2, 5, 15, 30s, then every 60s.
/// </summary>
public sealed class InfiniteRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] InitialDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
         TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        retryContext.PreviousRetryCount < InitialDelays.Length
            ? InitialDelays[retryContext.PreviousRetryCount]
            : TimeSpan.FromSeconds(60);
}
