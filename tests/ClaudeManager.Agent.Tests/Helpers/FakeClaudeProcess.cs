namespace ClaudeManager.Agent.Tests.Helpers;

/// <summary>
/// Fake IClaudeProcess that records start/kill calls and allows tests
/// to simulate output lines and process exit.
/// </summary>
public class FakeClaudeProcess : IClaudeProcess
{
    public string? SessionId { get; set; }
    public bool IsRunning { get; private set; }

    public bool StartWasCalled { get; private set; }
    public bool KillWasCalled  { get; private set; }
    public bool DisposeWasCalled { get; private set; }

    public event Func<string, Task>? OnOutputLine;
    public event Func<string, Task>? OnStderrLine;
    public event Func<int, Task>?    OnExit;

    public Task StartAsync(CancellationToken ct)
    {
        StartWasCalled = true;
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task KillAsync()
    {
        KillWasCalled = true;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeWasCalled = true;
        IsRunning = false;
        return ValueTask.CompletedTask;
    }

    public Task SimulateOutputLine(string rawJson) =>
        OnOutputLine?.Invoke(rawJson) ?? Task.CompletedTask;

    public Task SimulateStderrLine(string line) =>
        OnStderrLine?.Invoke(line) ?? Task.CompletedTask;

    public async Task SimulateExit(int exitCode = 0)
    {
        IsRunning = false;
        if (OnExit is not null)
            await OnExit(exitCode);
    }
}
