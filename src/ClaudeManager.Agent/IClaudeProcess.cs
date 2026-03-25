namespace ClaudeManager.Agent;

/// <summary>
/// Represents a single claude -p request/response cycle.
/// Abstracted so SessionProcessManager can be tested with fake processes.
/// </summary>
public interface IClaudeProcess : IAsyncDisposable
{
    string? SessionId { get; }
    bool IsRunning { get; }

    event Func<string, Task>? OnOutputLine;
    event Func<string, Task>? OnStderrLine;
    event Func<int, Task>?    OnExit;

    Task StartAsync(CancellationToken ct);
    Task KillAsync();
}
