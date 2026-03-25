namespace ClaudeManager.Shared.Dto;

public record StreamLineDto(
    string MachineId,
    string SessionId,
    string RawJson,
    DateTimeOffset Timestamp
);
