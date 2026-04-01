using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

/// <summary>
/// SSH host on which the AgentField swarm runs.
/// Commands are stored as a JSON array of {Label, Command} objects.
/// </summary>
[Table("SweAfHosts")]
public class SweAfHostEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(200)]
    public string DisplayName { get; set; } = "";

    /// <summary>Hostname or IP. Use "localhost" / "127.0.0.1" / "::1" to run commands locally.</summary>
    [MaxLength(500)]
    public string Host { get; set; } = "localhost";

    public int SshPort { get; set; } = 22;

    [MaxLength(100)]
    public string? SshUser { get; set; }

    [MaxLength(500)]
    public string? SshKeyPath { get; set; }

    [MaxLength(500)]
    public string? SshPassword { get; set; }

    /// <summary>Injected as ANTHROPIC_BASE_URL when executing commands.</summary>
    [MaxLength(500)]
    public string? AnthropicBaseUrl { get; set; }

    /// <summary>Injected as ANTHROPIC_API_KEY when executing commands.</summary>
    [MaxLength(500)]
    public string? AnthropicApiKey { get; set; }

    /// <summary>
    /// JSON array of host commands: [{"Label":"Start","Command":"docker compose up -d"}, ...]
    /// Parsed by SweAfHostService.GetCommands().
    /// </summary>
    public string? CommandsJson { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
