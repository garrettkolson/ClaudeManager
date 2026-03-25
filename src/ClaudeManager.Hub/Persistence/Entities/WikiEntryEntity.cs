using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("WikiEntries")]
public class WikiEntryEntity
{
    [Key]
    public long Id { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = default!;

    /// <summary>"project" | "bug" | "decision" | "note"</summary>
    [MaxLength(20)]
    public string Category { get; set; } = default!;

    /// <summary>Comma-separated tag list, e.g. "auth,jwt,backend"</summary>
    [MaxLength(500)]
    public string? Tags { get; set; }

    public string Content { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsArchived { get; set; }
}
