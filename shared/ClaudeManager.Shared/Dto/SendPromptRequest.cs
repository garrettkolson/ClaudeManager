namespace ClaudeManager.Shared.Dto;

public record SendPromptRequest(
    string MachineId,
    string SessionId,
    string Prompt
);
