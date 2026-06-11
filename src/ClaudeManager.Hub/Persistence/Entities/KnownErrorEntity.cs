using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("KnownErrors")]
public class KnownErrorEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>SHA256 hex of the normalized error message text.</summary>
    [MaxLength(64)]
    public string Fingerprint { get; set; } = default!;

    [MaxLength(2000)]
    public string Description { get; set; } = default!;

    public KnownErrorStatus Status { get; set; }

    [MaxLength(20)]
    public string? JiraKey { get; set; }

    [MaxLength(2000)]
    public string? MetadataJson { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? NextTriggerAfter { get; set; }
    public int TriggerCount { get; set; }
}

public enum KnownErrorStatus
{
    Pending = 0,
    Fixed = 1,
    Deferred = 2,
}
