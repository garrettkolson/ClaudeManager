using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ClaudeManager.Shared.Dto;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("ClaudeSessions")]
public class ClaudeSessionEntity
{
    [Key]
    public string SessionId { get; set; } = default!;
    public string MachineId { get; set; } = default!;
    public string WorkingDirectory { get; set; } = default!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    [MaxLength(4000)]
    public string? InitialPrompt { get; set; }
    public SessionStatus Status { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }

    public MachineAgentEntity Machine { get; set; } = default!;
    public ICollection<StreamedLineEntity> OutputLines { get; set; } = [];
}
