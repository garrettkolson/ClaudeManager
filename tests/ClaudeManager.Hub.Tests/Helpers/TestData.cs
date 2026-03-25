using ClaudeManager.Hub.Models;
using ClaudeManager.Hub.Persistence.Entities;
using ClaudeManager.Shared.Dto;

namespace ClaudeManager.Hub.Tests.Helpers;

public static class TestData
{
    public const string MachineId    = "machine-001";
    public const string ConnectionId = "conn-abc123";
    public const string SessionId    = "sess-xyz789";
    public const string WorkingDir   = "/projects/myapp";

    public static MachineAgentEntity AgentEntity(string? machineId = null) => new()
    {
        MachineId        = machineId ?? MachineId,
        DisplayName      = "Test Machine",
        Platform         = "win32",
        FirstConnectedAt = DateTimeOffset.UtcNow,
        LastConnectedAt  = DateTimeOffset.UtcNow,
        LastSeenAt       = DateTimeOffset.UtcNow,
    };

    public static ClaudeSessionEntity SessionEntity(SessionStatus status = SessionStatus.Active) => new()
    {
        SessionId        = SessionId,
        MachineId        = MachineId,
        WorkingDirectory = WorkingDir,
        StartedAt        = DateTimeOffset.UtcNow,
        Status           = status,
        LastActivityAt   = DateTimeOffset.UtcNow,
    };

    public static StreamedLine Line(StreamLineKind kind = StreamLineKind.AssistantToken) => new()
    {
        Timestamp  = DateTimeOffset.UtcNow,
        SessionId  = SessionId,
        Kind       = kind,
        Content    = "test content",
    };

    public static StreamLineDto LineDto(string? rawJson = null) => new(
        MachineId: MachineId,
        SessionId: SessionId,
        RawJson:   rawJson ?? "{\"type\":\"assistant\",\"content\":\"hi\"}",
        Timestamp: DateTimeOffset.UtcNow);
}
