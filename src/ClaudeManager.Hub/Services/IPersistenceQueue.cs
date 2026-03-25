using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Shared.Dto;

namespace ClaudeManager.Hub.Services;

public interface IPersistenceQueue
{
    void EnqueueUpsertAgent(MachineAgentEntity entity);
    void EnqueueUpsertSession(ClaudeSessionEntity entity);
    void EnqueueLine(StreamedLineEntity entity);
    void EnqueueSessionStatusChange(string sessionId, SessionStatus status, DateTimeOffset? endedAt);
}
