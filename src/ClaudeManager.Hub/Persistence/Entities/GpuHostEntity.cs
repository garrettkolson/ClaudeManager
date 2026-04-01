using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("GpuHosts")]
public class GpuHostEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>Unique human-readable slug used as a stable identifier.</summary>
    [MaxLength(100)]
    public string HostId { get; set; } = default!;

    [MaxLength(200)]
    public string DisplayName { get; set; } = default!;

    /// <summary>Hostname or IP address. Use "localhost" / "127.0.0.1" / "::1" to run commands locally.</summary>
    [MaxLength(500)]
    public string Host { get; set; } = default!;

    public int SshPort { get; set; } = 22;

    [MaxLength(100)]
    public string? SshUser { get; set; }

    [MaxLength(500)]
    public string? SshKeyPath { get; set; }

    [MaxLength(500)]
    public string? SshPassword { get; set; }

    /// <summary>
    /// Host port for the nginx reverse proxy container. Null means the proxy is not configured.
    /// The nginx container listens on this port and routes to per-model vLLM upstreams.
    /// </summary>
    public int? ProxyPort { get; set; }

    /// <summary>
    /// Indicates whether sudo/root privileges are required to execute Docker commands on this host.
    /// When true, commands will be prefixed with sudo and the SudoPassword will be used.
    /// </summary>
    public bool RequiresSudo { get; set; }

    /// <summary>
    /// The sudo password used for privilege escalation when RequiresSudo is true.
    /// This password is used with 'sudo -S' to authenticate Docker commands.
    /// </summary>
    [MaxLength(500)]
    public string? SudoPassword { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
