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

    public DateTimeOffset AddedAt { get; set; }
}
