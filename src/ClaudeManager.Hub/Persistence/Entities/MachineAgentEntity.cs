using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("MachineAgents")]
public class MachineAgentEntity
{
    [Key]
    public string MachineId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Platform { get; set; } = default!;
    public DateTimeOffset FirstConnectedAt { get; set; }
    public DateTimeOffset LastConnectedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public ICollection<ClaudeSessionEntity> Sessions { get; set; } = [];
}
