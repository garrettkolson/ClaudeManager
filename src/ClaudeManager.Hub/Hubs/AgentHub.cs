using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Services;
using ClaudeManager.Shared.Dto;
using Microsoft.AspNetCore.SignalR;
using SignalRHub = Microsoft.AspNetCore.SignalR.Hub;

namespace ClaudeManager.Hub.Hubs;

public class AgentHub : SignalRHub
{
    private readonly SessionStore _store;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(SessionStore store, ILogger<AgentHub> logger)
    {
        _store  = store;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Agent connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Find the machine that owned this connection and mark it offline
        var machineId = Context.Items["MachineId"] as string;
        if (machineId is not null)
        {
            _logger.LogInformation("Agent disconnected: {MachineId}", machineId);
            _store.MarkAgentDisconnected(machineId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    // ── Agent → Hub ───────────────────────────────────────────────────────────

    public Task RegisterAgent(AgentRegistration registration)
    {
        Context.Items["MachineId"] = registration.MachineId;
        _store.RegisterAgent(
            registration.MachineId,
            Context.ConnectionId,
            registration.DisplayName,
            registration.Platform);

        _logger.LogInformation("Agent registered: {MachineId} ({DisplayName})",
            registration.MachineId, registration.DisplayName);
        return Task.CompletedTask;
    }

    public Task SessionStarted(string machineId, string sessionId, string workingDirectory, string? initialPrompt)
    {
        _store.StartSession(machineId, sessionId, workingDirectory, initialPrompt);
        return Task.CompletedTask;
    }

    public Task StreamLine(StreamLineDto dto)
    {
        const int maxContent = 8000;
        var rawJson = dto.RawJson;
        var truncated = rawJson.Length > maxContent;
        var content = truncated ? rawJson[..maxContent] : rawJson;

        // Parse minimal fields from the raw JSON for display metadata
        var kind     = DetectKind(rawJson);
        var toolName = kind == StreamLineKind.ToolUse ? ExtractToolName(rawJson) : null;

        var line = new StreamedLine
        {
            Timestamp          = dto.Timestamp,
            SessionId          = dto.SessionId,
            Kind               = kind,
            Content            = content,
            IsContentTruncated = truncated,
            ToolName           = toolName,
        };

        _store.AppendLine(dto.MachineId, dto.SessionId, line);
        return Task.CompletedTask;
    }

    public Task SessionEnded(string machineId, string sessionId, int exitCode)
    {
        _store.EndSession(machineId, sessionId, exitCode);
        return Task.CompletedTask;
    }

    public Task Heartbeat(string machineId)
    {
        _store.UpdateHeartbeat(machineId);
        return Task.CompletedTask;
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static StreamLineKind DetectKind(string rawJson)
    {
        // Fast string-based detection to avoid full JSON parse on the hot path
        if (rawJson.Contains("\"type\":\"result\""))          return StreamLineKind.ResultSummary;
        if (rawJson.Contains("\"type\":\"assistant\""))       return StreamLineKind.AssistantToken;
        if (rawJson.Contains("\"type\":\"tool_use\""))        return StreamLineKind.ToolUse;
        if (rawJson.Contains("\"type\":\"tool_result\""))     return StreamLineKind.ToolResult;
        if (rawJson.Contains("\"stderr\""))                   return StreamLineKind.Error;
        return StreamLineKind.AssistantToken;
    }

    private static string? ExtractToolName(string rawJson)
    {
        // Extract "name":"<tool>" from tool_use events
        const string marker = "\"name\":\"";
        var idx = rawJson.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        var end   = rawJson.IndexOf('"', start);
        return end > start ? rawJson[start..end] : null;
    }
}
