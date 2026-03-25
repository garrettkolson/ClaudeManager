namespace ClaudeManager.Agent.Tests.Helpers;

/// <summary>
/// Factory that always returns the next FakeClaudeProcess from a queue.
/// Allows tests to pre-configure process behaviour.
/// </summary>
public class FakeClaudeProcessFactory : IClaudeProcessFactory
{
    private readonly Queue<FakeClaudeProcess> _queue = new();

    /// <summary>The last process created by this factory.</summary>
    public FakeClaudeProcess? LastCreated { get; private set; }

    /// <summary>All processes created by this factory, in order.</summary>
    public List<FakeClaudeProcess> Created { get; } = [];

    /// <summary>
    /// Enqueue a pre-configured process to be returned by the next Create call.
    /// If the queue is empty a new FakeClaudeProcess is created automatically.
    /// </summary>
    public void Enqueue(FakeClaudeProcess process) => _queue.Enqueue(process);

    public IClaudeProcess Create(string workingDirectory, string prompt, string? resumeSessionId)
    {
        var proc = _queue.Count > 0 ? _queue.Dequeue() : new FakeClaudeProcess();
        LastCreated = proc;
        Created.Add(proc);
        return proc;
    }
}
