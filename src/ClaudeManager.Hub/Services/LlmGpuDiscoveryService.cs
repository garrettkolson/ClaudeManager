using System.Diagnostics;
using ClaudeManager.Hub.Persistence.Entities;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Queries a GPU host via nvidia-smi to discover installed GPUs and their VRAM.
/// Uses Process.Start for localhost, SSH.NET for remote hosts.
/// </summary>
public class LlmGpuDiscoveryService
{
    private const string NvidiaSmiCommand =
        "nvidia-smi --query-gpu=index,name,memory.total,memory.free --format=csv,noheader,nounits";

    private readonly ILogger<LlmGpuDiscoveryService> _logger;

    public LlmGpuDiscoveryService(ILogger<LlmGpuDiscoveryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the list of GPUs on the host, or an error message if discovery fails.
    /// </summary>
    public async Task<(IReadOnlyList<GpuInfo> Gpus, string? Error)> DiscoverAsync(
        GpuHostEntity host, CancellationToken ct = default)
    {
        var output = IsLocalHost(host.Host)
            ? await RunLocalAsync(ct)
            : await RunViaSshAsync(host, ct);

        if (output.Error is not null)
            return ([], output.Error);

        var gpus = Parse(output.Stdout);
        return (gpus, null);
    }

    // ── Local execution ───────────────────────────────────────────────────────

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private async Task<(string Stdout, string? Error)> RunLocalAsync(CancellationToken ct)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/C {NvidiaSmiCommand}")
                : new ProcessStartInfo("bash", $"-c \"{NvidiaSmiCommand}\"");

            psi.UseShellExecute        = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            psi.CreateNoWindow         = true;

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                return ("", $"nvidia-smi exited {proc.ExitCode}: {stderr.Trim()}");

            return (stdout, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local nvidia-smi failed");
            return ("", ex.Message);
        }
    }

    // ── SSH execution ─────────────────────────────────────────────────────────

    private async Task<(string Stdout, string? Error)> RunViaSshAsync(
        GpuHostEntity host, CancellationToken ct)
    {
        var auth = BuildAuth(host);
        if (auth is null)
            return ("", "No SSH authentication configured (SshKeyPath or SshPassword required).");

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(host.Host, host.SshPort, host.SshUser!, auth);
            using var client = new SshClient(connInfo);

            await Task.Run(() => client.Connect(), ct);
            using var cmd = client.RunCommand(NvidiaSmiCommand);
            client.Disconnect();

            if (cmd.ExitStatus != 0 && !string.IsNullOrWhiteSpace(cmd.Error))
                return ("", $"nvidia-smi exited {cmd.ExitStatus}: {cmd.Error.Trim()}");

            return (cmd.Result, null);
        }
        catch (OperationCanceledException)
        {
            return ("", "Discovery cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH nvidia-smi failed for host {Host}", host.Host);
            return ("", ex.Message);
        }
    }

    private static AuthenticationMethod? BuildAuth(GpuHostEntity host)
    {
        if (host.SshKeyPath is not null)
        {
            var path = host.SshKeyPath.Replace("~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new PrivateKeyAuthenticationMethod(host.SshUser, new PrivateKeyFile(path));
        }

        if (host.SshPassword is not null)
            return new PasswordAuthenticationMethod(host.SshUser, host.SshPassword);

        return null;
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<GpuInfo> Parse(string stdout)
    {
        var results = new List<GpuInfo>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4) continue;

            if (!int.TryParse(parts[0], out var index))   continue;
            if (!int.TryParse(parts[2], out var total))   continue;
            if (!int.TryParse(parts[3], out var free))    continue;

            results.Add(new GpuInfo(index, parts[1], total, free));
        }
        return results;
    }
}
