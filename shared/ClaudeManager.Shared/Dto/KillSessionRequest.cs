namespace ClaudeManager.Shared.Dto;

public record KillSessionRequest(
    string MachineId,
    string SessionId
);
