using System.Collections.Concurrent;

namespace ClaudeManager.Hub.Models;

public class MachineAgent
{
    public string MachineId { get; init; } = default!;
    public string SignalRConnectionId { get; set; } = default!;
    public string DisplayName { get; init; } = default!;
    public string Platform { get; init; } = default!;
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }
    public bool IsOnline { get; set; }

    public ConcurrentDictionary<string, ClaudeSession> Sessions { get; } = new();
}
