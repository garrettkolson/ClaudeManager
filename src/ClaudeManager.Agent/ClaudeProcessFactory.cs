using Microsoft.Extensions.Logging;

namespace ClaudeManager.Agent;

/// <summary>
/// Production implementation: creates real ClaudeProcess instances.
/// </summary>
public class ClaudeProcessFactory : IClaudeProcessFactory
{
    private readonly string _binary;
    private readonly string? _mcpConfigPath;
    private readonly ILoggerFactory _loggerFactory;

    public ClaudeProcessFactory(string binary, ILoggerFactory loggerFactory, string? mcpConfigPath = null)
    {
        _binary        = binary;
        _mcpConfigPath = mcpConfigPath;
        _loggerFactory = loggerFactory;
    }

    public IClaudeProcess Create(string workingDirectory, string prompt, string? resumeSessionId) =>
        new ClaudeProcess(_binary, workingDirectory, prompt, resumeSessionId, _mcpConfigPath,
            _loggerFactory.CreateLogger<ClaudeProcess>());
}
