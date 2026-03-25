namespace ClaudeManager.Shared.Dto;

public record AgentRegistration(
    string MachineId,
    string DisplayName,
    string Platform,
    string AgentVersion
);
