using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ClaudeManager.Shared.Dto;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("StreamedLines")]
public class StreamedLineEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public string SessionId { get; set; } = default!;
    public DateTimeOffset Timestamp { get; set; }
    public StreamLineKind Kind { get; set; }
    [MaxLength(8000)]
    public string Content { get; set; } = default!;
    public bool IsContentTruncated { get; set; }
    [MaxLength(256)]
    public string? ToolName { get; set; }

    public ClaudeSessionEntity Session { get; set; } = default!;
}
