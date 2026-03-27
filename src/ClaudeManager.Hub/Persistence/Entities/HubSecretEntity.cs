using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("HubSecrets")]
public class HubSecretEntity
{
    [Key]
    [MaxLength(100)]
    public string Key { get; set; } = default!;

    [MaxLength(2000)]
    public string? Value { get; set; }
}
