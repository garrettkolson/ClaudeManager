namespace ClaudeManager.Agent;

/// <summary>
/// Verifies the claude binary is resolvable and authenticated before the agent connects.
/// Accepts an IProcessRunner so validation logic can be tested without spawning a real process.
/// </summary>
public class ClaudeValidator
{
    private readonly IProcessRunner _runner;

    public ClaudeValidator(IProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<(bool ok, string error)> ValidateAsync(string? binaryPath, CancellationToken ct)
    {
        var binary = ResolveBinary(binaryPath);
        if (binary is null)
            return (false, "Could not find 'claude' on PATH. Set ClaudeBinaryPath in appsettings.json.");

        try
        {
            var result = await _runner.RunAsync(
                binary,
                "-p \"ping\" --output-format json",
                TimeSpan.FromSeconds(15),
                ct);

            if (result.ExitCode != 0)
                return (false, $"claude exited with code {result.ExitCode}. Stderr: {result.Stderr.Trim()}");

            return (true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, "claude validation timed out (15s). Is the binary hanging or unauthenticated?");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch claude: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the claude binary path from config or PATH. Remains static for use before DI is built.
    /// </summary>
    public static string? ResolveBinary(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "claude.cmd", "claude.exe" }
            : new[] { "claude" };

        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in paths)
            foreach (var name in candidates)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }

        return null;
    }

    /// <summary>
    /// Returns the correct (FileName, Arguments) for Process.Start with UseShellExecute=false.
    /// On Windows, .cmd files must be invoked via cmd.exe /c.
    /// </summary>
    public static (string FileName, string Arguments) GetLaunchInfo(string binary, string arguments)
    {
        if (OperatingSystem.IsWindows() &&
            binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return ("cmd.exe", $"/c \"\"{binary}\" {arguments}\"");

        return (binary, arguments);
    }
}
