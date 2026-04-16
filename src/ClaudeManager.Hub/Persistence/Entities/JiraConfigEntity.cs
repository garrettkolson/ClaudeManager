using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("JiraConfigs")]
public class JiraConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(500)]
    public string BaseUrl { get; set; } = "";

    [MaxLength(500)]
    public string Email { get; set; } = "";

    [MaxLength(500)]
    public string? ApiToken { get; set; }

    [MaxLength(50)]
    public string? DefaultProjectKey { get; set; }

    [MaxLength(1000)]
    public string? DefaultJql { get; set; }

    [MaxLength(1000)]
    public string? DefaultRepoUrl { get; set; }

    [MaxLength(100)]
    public string OnDeckStatusName { get; set; } = "On Deck";

    [MaxLength(100)]
    public string ReviewStatusName { get; set; } = "Review";

    public int PollingIntervalSecs { get; set; } = 60;

    [MaxLength(500)]
    public string? WebhookSecret { get; set; }

    [NotMapped]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(ApiToken);
}
