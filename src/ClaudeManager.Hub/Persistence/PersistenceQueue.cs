using System.Threading.Channels;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Hub.Services;
using ClaudeManager.Shared.Dto;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Persistence;

// ── Work items ────────────────────────────────────────────────────────────────

abstract record PersistenceWork;
record UpsertAgentWork(MachineAgentEntity Entity)    : PersistenceWork;
record UpsertSessionWork(ClaudeSessionEntity Entity) : PersistenceWork;
record WriteLineWork(StreamedLineEntity Entity)      : PersistenceWork;
record SessionStatusWork(string SessionId, SessionStatus Status, DateTimeOffset? EndedAt) : PersistenceWork;

// ── Queue ─────────────────────────────────────────────────────────────────────

public class PersistenceQueue : BackgroundService, IPersistenceQueue
{
    private readonly Channel<PersistenceWork> _channel = Channel.CreateBounded<PersistenceWork>(
        new BoundedChannelOptions(2000)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly ILogger<PersistenceQueue> _logger;

    public PersistenceQueue(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        ILogger<PersistenceQueue> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public void EnqueueUpsertAgent(MachineAgentEntity entity)   => TryEnqueue(new UpsertAgentWork(entity));
    public void EnqueueUpsertSession(ClaudeSessionEntity entity) => TryEnqueue(new UpsertSessionWork(entity));
    public void EnqueueLine(StreamedLineEntity entity)           => TryEnqueue(new WriteLineWork(entity));
    public void EnqueueSessionStatusChange(string sessionId, SessionStatus status, DateTimeOffset? endedAt)
        => TryEnqueue(new SessionStatusWork(sessionId, status, endedAt));

    private void TryEnqueue(PersistenceWork work)
    {
        if (!_channel.Writer.TryWrite(work))
            _logger.LogWarning("Persistence channel is full; dropping {WorkType} write", work.GetType().Name);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var work in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                await ProcessWork(db, work, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Persistence write failed for {WorkType}", work.GetType().Name);
            }
        }
    }

    private static async Task ProcessWork(ClaudeManagerDbContext db, PersistenceWork work, CancellationToken ct)
    {
        switch (work)
        {
            case UpsertAgentWork { Entity: var agent }:
            {
                var exists = await db.MachineAgents.AnyAsync(a => a.MachineId == agent.MachineId, ct);
                if (exists)
                {
                    await db.MachineAgents
                        .Where(a => a.MachineId == agent.MachineId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.DisplayName,     agent.DisplayName)
                            .SetProperty(a => a.LastConnectedAt, agent.LastConnectedAt)
                            .SetProperty(a => a.LastSeenAt,      agent.LastSeenAt), ct);
                }
                else
                {
                    db.MachineAgents.Add(agent);
                }
                break;
            }

            case UpsertSessionWork { Entity: var session }:
            {
                var exists = await db.ClaudeSessions.AnyAsync(s => s.SessionId == session.SessionId, ct);
                if (exists)
                    db.ClaudeSessions.Update(session);
                else
                    db.ClaudeSessions.Add(session);
                break;
            }

            case WriteLineWork { Entity: var line }:
            {
                db.StreamedLines.Add(line);
                // Bump LastActivityAt without loading the entity
                await db.ClaudeSessions
                    .Where(s => s.SessionId == line.SessionId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.LastActivityAt, line.Timestamp), ct);
                break;
            }

            case SessionStatusWork { SessionId: var sid, Status: var status, EndedAt: var endedAt }:
            {
                await db.ClaudeSessions
                    .Where(s => s.SessionId == sid)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status,  status)
                        .SetProperty(x => x.EndedAt, endedAt), ct);
                break;
            }
        }
    }
}
