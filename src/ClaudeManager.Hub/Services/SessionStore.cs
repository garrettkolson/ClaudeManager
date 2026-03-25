using System.Collections.Concurrent;
using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Shared.Dto;

namespace ClaudeManager.Hub.Services;

public class SessionStore
{
    private readonly ConcurrentDictionary<string, MachineAgent> _machines = new();
    private readonly DashboardNotifier _notifier;

    // Injected lazily to break circular dependency with PersistenceQueue
    private IPersistenceQueue? _persistenceQueue;

    public SessionStore(DashboardNotifier notifier)
    {
        _notifier = notifier;
    }

    public void SetPersistenceQueue(IPersistenceQueue queue) => _persistenceQueue = queue;

    // ── Agent lifecycle ──────────────────────────────────────────────────────

    public MachineAgent RegisterAgent(string machineId, string connectionId, string displayName, string platform)
    {
        var now = DateTimeOffset.UtcNow;
        var agent = _machines.AddOrUpdate(
            machineId,
            _ => new MachineAgent
            {
                MachineId            = machineId,
                SignalRConnectionId  = connectionId,
                DisplayName          = displayName,
                Platform             = platform,
                ConnectedAt          = now,
                LastHeartbeatAt      = now,
                IsOnline             = true,
            },
            (_, existing) =>
            {
                existing.SignalRConnectionId = connectionId;
                existing.ConnectedAt         = now;
                existing.LastHeartbeatAt     = now;
                existing.IsOnline            = true;
                return existing;
            });

        _persistenceQueue?.EnqueueUpsertAgent(new MachineAgentEntity
        {
            MachineId        = machineId,
            DisplayName      = displayName,
            Platform         = platform,
            FirstConnectedAt = now,
            LastConnectedAt  = now,
            LastSeenAt       = now,
        });

        _notifier.NotifyAgentConnected(agent);
        return agent;
    }

    public void MarkAgentDisconnected(string machineId)
    {
        if (!_machines.TryGetValue(machineId, out var agent)) return;
        agent.IsOnline = false;

        foreach (var session in agent.Sessions.Values.Where(s => s.Status == SessionStatus.Active))
        {
            session.Status         = SessionStatus.Disconnected;
            session.IsProcessRunning = false;
            _persistenceQueue?.EnqueueSessionStatusChange(session.SessionId, SessionStatus.Disconnected, null);
        }

        _notifier.NotifyAgentDisconnected(machineId);
    }

    public void UpdateHeartbeat(string machineId)
    {
        if (!_machines.TryGetValue(machineId, out var agent)) return;
        agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
        _notifier.NotifyHeartbeat(machineId);
    }

    // ── Session lifecycle ────────────────────────────────────────────────────

    public ClaudeSession StartSession(string machineId, string sessionId, string workingDirectory, string? initialPrompt)
    {
        if (!_machines.TryGetValue(machineId, out var agent))
            throw new InvalidOperationException($"Machine {machineId} not registered.");

        var session = new ClaudeSession
        {
            SessionId        = sessionId,
            MachineId        = machineId,
            WorkingDirectory = workingDirectory,
            StartedAt        = DateTimeOffset.UtcNow,
            InitialPrompt    = initialPrompt,
            Status           = SessionStatus.Active,
            IsProcessRunning = true,
        };

        agent.Sessions[sessionId] = session;

        _persistenceQueue?.EnqueueUpsertSession(new ClaudeSessionEntity
        {
            SessionId        = sessionId,
            MachineId        = machineId,
            WorkingDirectory = workingDirectory,
            StartedAt        = session.StartedAt,
            InitialPrompt    = initialPrompt?[..Math.Min(initialPrompt.Length, 4000)],
            Status           = SessionStatus.Active,
            LastActivityAt   = session.StartedAt,
        });

        _notifier.NotifySessionStarted(session);
        return session;
    }

    public void AppendLine(string machineId, string sessionId, StreamedLine line)
    {
        if (!TryGetSession(machineId, sessionId, out var session)) return;

        session.AppendLine(line);
        _persistenceQueue?.EnqueueLine(new StreamedLineEntity
        {
            SessionId          = sessionId,
            Timestamp          = line.Timestamp,
            Kind               = line.Kind,
            Content            = line.Content,
            IsContentTruncated = line.IsContentTruncated,
            ToolName           = line.ToolName,
        });

        _notifier.NotifyLineStreamed(line);
    }

    public void EndSession(string machineId, string sessionId, int exitCode)
    {
        if (!TryGetSession(machineId, sessionId, out var session)) return;

        var endedAt = DateTimeOffset.UtcNow;
        session.EndedAt          = endedAt;
        session.Status           = SessionStatus.Ended;
        session.IsProcessRunning = false;

        _persistenceQueue?.EnqueueSessionStatusChange(sessionId, SessionStatus.Ended, endedAt);
        _notifier.NotifySessionEnded(machineId, sessionId, exitCode);
    }

    public void SetProcessRunning(string machineId, string sessionId, bool running)
    {
        if (!TryGetSession(machineId, sessionId, out var session)) return;
        session.IsProcessRunning = running;
        _notifier.NotifySessionStatusChanged(session);
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public IReadOnlyList<MachineAgent> GetAllMachines() => _machines.Values.ToList();

    public MachineAgent? GetMachine(string machineId) =>
        _machines.TryGetValue(machineId, out var a) ? a : null;

    public ClaudeSession? GetSession(string machineId, string sessionId)
    {
        TryGetSession(machineId, sessionId, out var session);
        return session;
    }

    public string? GetConnectionId(string machineId) =>
        _machines.TryGetValue(machineId, out var a) && a.IsOnline ? a.SignalRConnectionId : null;

    // ── DB recovery helpers ──────────────────────────────────────────────────

    public void EnsureAgentFromDb(MachineAgentEntity entity)
    {
        _machines.TryAdd(entity.MachineId, new MachineAgent
        {
            MachineId           = entity.MachineId,
            DisplayName         = entity.DisplayName,
            Platform            = entity.Platform,
            SignalRConnectionId = string.Empty,
            ConnectedAt         = entity.LastConnectedAt,
            LastHeartbeatAt     = entity.LastSeenAt,
            IsOnline            = false,
        });
    }

    public void RestoreSessionFromDb(ClaudeSessionEntity entity, IEnumerable<StreamedLine> lines)
    {
        if (!_machines.TryGetValue(entity.MachineId, out var agent)) return;

        var session = new ClaudeSession
        {
            SessionId        = entity.SessionId,
            MachineId        = entity.MachineId,
            WorkingDirectory = entity.WorkingDirectory,
            StartedAt        = entity.StartedAt,
            EndedAt          = entity.EndedAt,
            InitialPrompt    = entity.InitialPrompt,
            Status           = entity.Status,
            IsProcessRunning = false,
        };
        session.SetOutputLines(lines);

        agent.Sessions.TryAdd(entity.SessionId, session);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool TryGetSession(string machineId, string sessionId, out ClaudeSession session)
    {
        session = default!;
        if (!_machines.TryGetValue(machineId, out var agent)) return false;
        return agent.Sessions.TryGetValue(sessionId, out session!);
    }
}
