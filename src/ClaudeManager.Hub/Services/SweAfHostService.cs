using System.Diagnostics;
using System.Text.Json;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// DB-backed service for AgentField hosts.
/// Provides CRUD for SweAfHostEntity and executes shell commands on those hosts
/// via local Process.Start (localhost) or SSH.NET (remote).
/// </summary>
public class SweAfHostService
{
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly ILogger<SweAfHostService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public SweAfHostService(
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        ILogger<SweAfHostService> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    // ── Command list helper ───────────────────────────────────────────────────

    /// <summary>Parses CommandsJson into a list of SweAfHostCommand objects.</summary>
    public static List<SweAfHostCommand> GetCommands(SweAfHostEntity host)
    {
        if (string.IsNullOrWhiteSpace(host.CommandsJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SweAfHostCommand>>(
                host.CommandsJson, _jsonOptions) ?? [];
        }
        catch { return []; }
    }

    /// <summary>Serializes a list of commands back to JSON for storage.</summary>
    public static string? SerializeCommands(IEnumerable<SweAfHostCommand> commands)
    {
        var list = commands.Where(c => !string.IsNullOrWhiteSpace(c.Label)).ToList();
        return list.Count > 0 ? JsonSerializer.Serialize(list) : null;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<List<SweAfHostEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SweAfHosts.OrderBy(h => h.DisplayName).ToListAsync(ct);
    }

    public async Task<SweAfHostEntity?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SweAfHosts.FindAsync([id], ct);
    }

    public async Task<SweAfHostEntity> CreateAsync(
        string displayName,
        string? anthropicBaseUrl = null, string? anthropicApiKey = null,
        List<SweAfHostCommand>? commands = null,
        CancellationToken ct = default)
    {
        var entity = new SweAfHostEntity
        {
            DisplayName      = displayName.Trim(),
            Host             = "control-plane",
            SshPort          = 8080,
            SshUser          = null,
            SshKeyPath       = null,
            SshPassword      = null,
            AnthropicBaseUrl = NullIfBlank(anthropicBaseUrl),
            AnthropicApiKey  = NullIfBlank(anthropicApiKey),
            CommandsJson     = commands is { Count: > 0 } ? SerializeCommands(commands) : null,
            AddedAt          = DateTimeOffset.UtcNow,
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SweAfHosts.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    /// <summary>
    /// Creates a host with SSH configuration (legacy method, kept for compatibility).
    /// </summary>
    public async Task<SweAfHostEntity> CreateAsync(
        string displayName, string host, int sshPort = 22,
        string? sshUser = null, string? sshKeyPath = null, string? sshPassword = null,
        string? anthropicBaseUrl = null, string? anthropicApiKey = null,
        List<SweAfHostCommand>? commands = null,
        CancellationToken ct = default)
    {
        var entity = new SweAfHostEntity
        {
            DisplayName      = displayName.Trim(),
            Host             = host.Trim(),
            SshPort          = sshPort,
            SshUser          = NullIfBlank(sshUser),
            SshKeyPath       = NullIfBlank(sshKeyPath),
            SshPassword      = NullIfBlank(sshPassword),
            AnthropicBaseUrl = NullIfBlank(anthropicBaseUrl),
            AnthropicApiKey  = NullIfBlank(anthropicApiKey),
            CommandsJson     = commands is { Count: > 0 } ? SerializeCommands(commands) : null,
            AddedAt          = DateTimeOffset.UtcNow,
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SweAfHosts.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(SweAfHostEntity host, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SweAfHosts.Update(host);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.SweAfHosts.FindAsync([id], ct);
        if (entity is not null)
        {
            db.SweAfHosts.Remove(entity);
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Command execution ─────────────────────────────────────────────────────

    /// <summary>Runs a shell command on the given host (local or SSH).</summary>
    public Task<(bool Success, string? Error)> RunCommandAsync(
        SweAfHostEntity host, string command, string label,
        CancellationToken ct = default)
    {
        _logger.LogInformation("SWE-AF host [{Label}]: {Host}", label, host.Host);

        return IsLocalHost(host.Host)
            ? Task.FromResult(RunLocal(host, command, label))
            : RunViaSshAsync(host, command, label, ct);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1";

    private (bool, string?) RunLocal(SweAfHostEntity host, string command, string label)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe", $"/C {command}")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo("bash", $"-c \"{command.Replace("\"", "\\\"")}\"")
                {
                    UseShellExecute = false,
                };
            }

            if (!string.IsNullOrWhiteSpace(host.AnthropicBaseUrl))
                psi.Environment["ANTHROPIC_BASE_URL"] = host.AnthropicBaseUrl;
            if (!string.IsNullOrWhiteSpace(host.AnthropicApiKey))
                psi.Environment["ANTHROPIC_API_KEY"] = host.AnthropicApiKey;

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            _logger.LogInformation("SWE-AF host [{Label}] completed locally", label);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SWE-AF host [{Label}] failed locally", label);
            return (false, ex.Message);
        }
    }

    private async Task<(bool, string?)> RunViaSshAsync(
        SweAfHostEntity host, string command, string label, CancellationToken ct)
    {
        var auth = BuildAuth(host);
        if (auth is null)
            return (false, "No SSH authentication configured (set SshKeyPath or SshPassword).");

        try
        {
            var connInfo = new Renci.SshNet.ConnectionInfo(host.Host, host.SshPort, host.SshUser, auth);
            using var client = new SshClient(connInfo);

            await Task.Run(() => client.Connect(), ct);
            _logger.LogInformation("SSH connected to {Host} for [{Label}]", host.Host, label);

            using var cmd = client.RunCommand(InjectEnvVars(host, command));
            client.Disconnect();

            // Non-zero exit is only a hard failure when stderr is also non-empty.
            // Stop-like commands can return non-zero when nothing was running — acceptable.
            if (cmd.ExitStatus != 0 && !string.IsNullOrWhiteSpace(cmd.Error))
            {
                var err = cmd.Error.Trim();
                _logger.LogWarning(
                    "SWE-AF host [{Label}] exited {Code}: {Error}", label, cmd.ExitStatus, err);
                return (false, $"Command failed (exit {cmd.ExitStatus}): {err}");
            }

            _logger.LogInformation(
                "SWE-AF host [{Label}] succeeded on {Host}", label, host.Host);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SWE-AF host [{Label}] SSH failed", label);
            return (false, ex.Message);
        }
    }

    private static string InjectEnvVars(SweAfHostEntity host, string command)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(host.AnthropicBaseUrl))
            parts.Add($"ANTHROPIC_BASE_URL={QuoteForShell(host.AnthropicBaseUrl)}");
        if (!string.IsNullOrWhiteSpace(host.AnthropicApiKey))
            parts.Add($"ANTHROPIC_API_KEY={QuoteForShell(host.AnthropicApiKey)}");
        return parts.Count == 0 ? command : string.Join(" ", parts) + " " + command;
    }

    private static string QuoteForShell(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    private static AuthenticationMethod? BuildAuth(SweAfHostEntity host)
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

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
