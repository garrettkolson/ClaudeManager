namespace ClaudeManager.Agent;

public record ProcessResult(int ExitCode, string Stderr);

/// <summary>
/// Runs an external process and returns its exit code and stderr output.
/// Abstracted so ClaudeValidator can be tested without spawning real processes.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the given binary with the given arguments.
    /// Throws <see cref="OperationCanceledException"/> if the timeout elapses.
    /// </summary>
    Task<ProcessResult> RunAsync(string binary, string arguments, TimeSpan timeout, CancellationToken ct);
}
