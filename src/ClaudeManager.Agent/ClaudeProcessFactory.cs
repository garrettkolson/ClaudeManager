using Microsoft.Extensions.Logging;

namespace ClaudeManager.Agent;

/// <summary>
/// Production implementation: creates real ClaudeProcess instances.
/// </summary>
public class ClaudeProcessFactory : IClaudeProcessFactory
{
    private readonly string _binary;
    private readonly ILoggerFactory _loggerFactory;

    public ClaudeProcessFactory(string binary, ILoggerFactory loggerFactory)
    {
        _binary       = binary;
        _loggerFactory = loggerFactory;
    }

    public IClaudeProcess Create(string workingDirectory, string prompt, string? resumeSessionId) =>
        new ClaudeProcess(_binary, workingDirectory, prompt, resumeSessionId,
            _loggerFactory.CreateLogger<ClaudeProcess>());
}
