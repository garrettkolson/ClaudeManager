namespace ClaudeManager.Shared.Dto;

public record StartSessionRequest(
    string MachineId,
    string WorkingDirectory,
    string InitialPrompt,
    string? ResumeSessionId  // null = new session
);
