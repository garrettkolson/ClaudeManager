using ClaudeManager.Shared.Dto;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeManager.Hub.Services;

public class AgentCommandService
{
    private readonly IHubContext<Hubs.AgentHub> _hubContext;
    private readonly SessionStore _store;
    private readonly ILogger<AgentCommandService> _logger;

    public AgentCommandService(
        IHubContext<Hubs.AgentHub> hubContext,
        SessionStore store,
        ILogger<AgentCommandService> logger)
    {
        _hubContext = hubContext;
        _store      = store;
        _logger     = logger;
    }

    public async Task<bool> StartSessionAsync(StartSessionRequest request)
    {
        var connId = _store.GetConnectionId(request.MachineId);
        if (connId is null)
        {
            _logger.LogWarning("StartSession: machine {MachineId} is not online", request.MachineId);
            return false;
        }

        await _hubContext.Clients.Client(connId).SendAsync("StartSession", request);
        return true;
    }

    public async Task<bool> SendPromptAsync(SendPromptRequest request)
    {
        var connId = _store.GetConnectionId(request.MachineId);
        if (connId is null)
        {
            _logger.LogWarning("SendPrompt: machine {MachineId} is not online", request.MachineId);
            return false;
        }

        await _hubContext.Clients.Client(connId).SendAsync("SendPrompt", request);
        return true;
    }

    public async Task<bool> KillSessionAsync(string machineId, string sessionId)
    {
        var connId = _store.GetConnectionId(machineId);
        if (connId is null)
        {
            _logger.LogWarning("KillSession: machine {MachineId} is not online", machineId);
            return false;
        }

        await _hubContext.Clients.Client(connId).SendAsync("KillSession",
            new KillSessionRequest(machineId, sessionId));
        return true;
    }
}
