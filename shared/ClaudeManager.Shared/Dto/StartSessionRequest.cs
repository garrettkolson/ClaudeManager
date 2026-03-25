namespace ClaudeManager.Shared.Dto;

public record StartSessionRequest(
    string MachineId,
    string WorkingDirectory,
    string InitialPrompt,
    string? ResumeSessionId,  // null = new session
    string? SystemContext = null  // Hub-injected context (wiki entries etc.); agent uses for claude but does not echo back
);
