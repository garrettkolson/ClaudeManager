using System.Diagnostics;

namespace ClaudeManager.Agent;

/// <summary>
/// Verifies the claude binary is resolvable and authenticated before the agent connects.
/// </summary>
public static class ClaudeValidator
{
    public static async Task<(bool ok, string error)> ValidateAsync(string? binaryPath, CancellationToken ct)
    {
        var binary = ResolveBinary(binaryPath);
        if (binary is null)
            return (false, "Could not find 'claude' on PATH. Set ClaudeBinaryPath in appsettings.json.");

        // Run a minimal one-shot invocation to check authentication
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = binary,
            Arguments              = "-p \"ping\" --output-format json",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        try
        {
            proc.Start();

            // Give claude 15 seconds to respond
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            await proc.WaitForExitAsync(cts.Token);

            if (proc.ExitCode != 0)
            {
                var stderr = await proc.StandardError.ReadToEndAsync(ct);
                return (false, $"claude exited with code {proc.ExitCode}. Stderr: {stderr.Trim()}");
            }

            return (true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (false, "claude validation timed out (15s). Is the binary hanging or unauthenticated?");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch claude: {ex.Message}");
        }
    }

    public static string? ResolveBinary(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // Search PATH
        var binary = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var paths  = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in paths)
        {
            var candidate = Path.Combine(dir, binary);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
