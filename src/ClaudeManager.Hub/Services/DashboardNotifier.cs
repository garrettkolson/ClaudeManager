using ClaudeManager.Hub.Models;

namespace ClaudeManager.Hub.Services;

public class DashboardNotifier
{
    public event Action<MachineAgent>?         AgentConnected;
    public event Action<string>?               AgentDisconnected;   // machineId
    public event Action<StreamedLine>?         LineStreamed;
    public event Action<ClaudeSession>?        SessionStarted;
    public event Action<string, string, int>?  SessionEnded;        // machineId, sessionId, exitCode
    public event Action<string>?               HeartbeatReceived;   // machineId
    public event Action<ClaudeSession>?        SessionStatusChanged;

    public void NotifyAgentConnected(MachineAgent agent)         => AgentConnected?.Invoke(agent);
    public void NotifyAgentDisconnected(string machineId)        => AgentDisconnected?.Invoke(machineId);
    public void NotifyLineStreamed(StreamedLine line)             => LineStreamed?.Invoke(line);
    public void NotifySessionStarted(ClaudeSession session)      => SessionStarted?.Invoke(session);
    public void NotifySessionEnded(string machineId, string sessionId, int exitCode)
        => SessionEnded?.Invoke(machineId, sessionId, exitCode);
    public void NotifyHeartbeat(string machineId)                => HeartbeatReceived?.Invoke(machineId);
    public void NotifySessionStatusChanged(ClaudeSession session) => SessionStatusChanged?.Invoke(session);
}
