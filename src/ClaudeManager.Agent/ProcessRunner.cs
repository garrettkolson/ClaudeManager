using System.Diagnostics;

namespace ClaudeManager.Agent;

/// <summary>
/// Production implementation of IProcessRunner using System.Diagnostics.Process.
/// </summary>
public class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string binary, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var proc = new Process();
        var (fileName, launchArgs) = ClaudeValidator.GetLaunchInfo(binary, arguments);
        proc.StartInfo = new ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = launchArgs,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        proc.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        return new ProcessResult(proc.ExitCode, stderr);
    }
}
