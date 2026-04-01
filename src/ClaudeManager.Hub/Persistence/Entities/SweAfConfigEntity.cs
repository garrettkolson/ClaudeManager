using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

/// <summary>
/// Stores the AgentField connection config in SQLite.
/// A single row (Id = 1) is always upserted; the table is never multi-row.
/// </summary>
[Table("SweAfConfigs")]
public class SweAfConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(500)]
    public string BaseUrl { get; set; } = "";

    [MaxLength(500)]
    public string? ApiKey { get; set; }

    [MaxLength(500)]
    public string? WebhookSecret { get; set; }

    [MaxLength(500)]
    public string? HubPublicUrl { get; set; }

    [MaxLength(50)]
    public string Runtime { get; set; } = "claude_code";

    [MaxLength(100)]
    public string? ModelDefault { get; set; }

    [MaxLength(100)]
    public string? ModelCoder { get; set; }

    [MaxLength(100)]
    public string? ModelQa { get; set; }

    /// <summary>Default repo URL pre-filled in the New Build modal.</summary>
    [MaxLength(1000)]
    public string? DefaultRepoUrl { get; set; }

    /// <summary>
    /// Personal access token (GitHub, GitLab, etc.) injected into the build trigger payload
    /// so the agent swarm can clone and push to private repositories.
    /// </summary>
    [MaxLength(500)]
    public string? RepositoryApiToken { get; set; }

    [NotMapped]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
