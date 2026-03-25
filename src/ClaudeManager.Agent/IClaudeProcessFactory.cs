namespace ClaudeManager.Agent;

/// <summary>
/// Creates IClaudeProcess instances. Abstracted so SessionProcessManager
/// can be tested by injecting a factory that returns fake processes.
/// </summary>
public interface IClaudeProcessFactory
{
    IClaudeProcess Create(string workingDirectory, string prompt, string? resumeSessionId);
}
