using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ClaudeManager.Hub.Services;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("SweAfJobs")]
public class SweAfJobEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>The execution_id returned by AgentField on job submission.</summary>
    [MaxLength(256)]
    public string ExternalJobId { get; set; } = default!;

    [MaxLength(4000)]
    public string Goal { get; set; } = default!;

    [MaxLength(1000)]
    public string RepoUrl { get; set; } = default!;

    public BuildStatus Status { get; set; }

    /// <summary>"hub" when triggered from this dashboard; null for externally triggered runs.</summary>
    [MaxLength(100)]
    public string? TriggeredBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>JSON array of pull-request URLs, e.g. ["https://github.com/…/pull/42"].</summary>
    [MaxLength(4000)]
    public string? PrUrls { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Latest log output, kept fresh by webhook execution_updated events.
    /// No max length — log content can be large.
    /// </summary>
    public string? Logs { get; set; }
}
