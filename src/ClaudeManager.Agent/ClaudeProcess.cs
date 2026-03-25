using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeManager.Shared.Dto;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Agent;

/// <summary>
/// Manages a single claude -p request/response cycle.
/// Claude exits after each response; use --resume for follow-up prompts.
/// </summary>
public sealed class ClaudeProcess : IAsyncDisposable
{
    private readonly string _binary;
    private readonly string _workingDirectory;
    private readonly string _prompt;
    private readonly string? _resumeSessionId;
    private readonly ILogger<ClaudeProcess> _logger;

    private Process? _process;
    private bool _disposed;

    public string? SessionId { get; private set; }     // Set from system/init JSON line
    public bool IsRunning    { get; private set; }

    public event Func<string, Task>? OnOutputLine;     // raw JSON line from stdout
    public event Func<string, Task>? OnStderrLine;     // line from stderr
    public event Func<int, Task>?    OnExit;           // process exit code

    public ClaudeProcess(
        string binary,
        string workingDirectory,
        string prompt,
        string? resumeSessionId,
        ILogger<ClaudeProcess> logger)
    {
        _binary          = binary;
        _workingDirectory = workingDirectory;
        _prompt          = prompt;
        _resumeSessionId = resumeSessionId;
        _logger          = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var args = BuildArguments();
        _logger.LogInformation("Launching claude: {Args} in {Dir}", args, _workingDirectory);

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = _binary,
                Arguments              = args,
                WorkingDirectory       = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            },
            EnableRaisingEvents = true,
        };

        _process.Start();
        IsRunning = true;

        AssignToJobObject();

        // Read stdout and stderr concurrently
        _ = ReadStdoutAsync(ct);
        _ = ReadStderrAsync(ct);

        return Task.CompletedTask;
    }

    public async Task KillAsync()
    {
        if (_process is null || _process.HasExited) return;
        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error killing claude process");
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private string BuildArguments()
    {
        // Escape the prompt for CLI: wrap in double quotes, escape inner quotes
        var escaped = _prompt.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var args    = $"-p \"{escaped}\" --output-format stream-json";

        if (_resumeSessionId is not null)
            args += $" --resume {_resumeSessionId}";

        return args;
    }

    private async Task ReadStdoutAsync(CancellationToken ct)
    {
        try
        {
            bool firstLine = true;
            while (await _process!.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Extract session_id from the first system/init line
                if (firstLine)
                {
                    firstLine = false;
                    TryExtractSessionId(line);
                    CheckForResumeError(line);
                }

                if (OnOutputLine is not null)
                    await OnOutputLine.Invoke(line);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading claude stdout");
        }
        finally
        {
            await FinalizeAsync();
        }
    }

    private async Task ReadStderrAsync(CancellationToken ct)
    {
        try
        {
            while (await _process!.StandardError.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _logger.LogWarning("claude stderr: {Line}", line);
                if (OnStderrLine is not null)
                    await OnStderrLine.Invoke(line);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading claude stderr");
        }
    }

    private async Task FinalizeAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        try
        {
            if (_process is not null && !_process.HasExited)
                await _process.WaitForExitAsync();

            var exitCode = _process?.ExitCode ?? -1;
            _logger.LogInformation("claude process exited with code {ExitCode}", exitCode);

            if (OnExit is not null)
                await OnExit.Invoke(exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizing claude process");
        }
    }

    private void TryExtractSessionId(string json)
    {
        // system/init line: {"type":"system","subtype":"init","session_id":"<id>", ...}
        const string marker = "\"session_id\":\"";
        var idx = json.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return;

        var start = idx + marker.Length;
        var end   = json.IndexOf('"', start);
        if (end > start)
            SessionId = json[start..end];
    }

    private void CheckForResumeError(string json)
    {
        // Detect a result/error on the very first line, which indicates --resume failed
        if (_resumeSessionId is null) return;
        if (json.Contains("\"type\":\"result\"") && json.Contains("\"subtype\":\"error\""))
            _logger.LogWarning("--resume {SessionId} may have failed; first line is an error result", _resumeSessionId);
    }

    /// <summary>
    /// On Windows, assigns the child process to a Job Object so it is killed
    /// automatically if this agent process exits for any reason (including crashes).
    /// No-op on non-Windows platforms (use process groups / prctl there).
    /// </summary>
    private void AssignToJobObject()
    {
        if (!OperatingSystem.IsWindows() || _process is null) return;

        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = 0x2000, // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            var infoSize = Marshal.SizeOf(info);
            var infoPtr  = Marshal.AllocHGlobal(infoSize);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                SetInformationJobObject(job, 9, infoPtr, (uint)infoSize);
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            AssignProcessToJobObject(job, _process.Handle);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not assign claude process to Job Object (non-critical)");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await KillAsync();
        _process?.Dispose();
    }

    // ── Windows Job Object P/Invoke ───────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
