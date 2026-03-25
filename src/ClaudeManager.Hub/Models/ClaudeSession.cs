using ClaudeManager.Shared.Dto;

namespace ClaudeManager.Hub.Models;

public class ClaudeSession
{
    public string SessionId { get; set; } = default!;
    public string MachineId { get; init; } = default!;
    public string WorkingDirectory { get; init; } = default!;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? InitialPrompt { get; init; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public bool IsProcessRunning { get; set; }

    private readonly List<StreamedLine> _outputLines = [];
    private readonly object _lock = new();

    public IReadOnlyList<StreamedLine> OutputLines
    {
        get { lock (_lock) { return _outputLines.ToList(); } }
    }

    public void AppendLine(StreamedLine line)
    {
        lock (_lock)
        {
            if (_outputLines.Count >= 2000)
                _outputLines.RemoveAt(0);
            _outputLines.Add(line);
        }
    }

    public void SetOutputLines(IEnumerable<StreamedLine> lines)
    {
        lock (_lock)
        {
            _outputLines.Clear();
            _outputLines.AddRange(lines);
        }
    }
}
