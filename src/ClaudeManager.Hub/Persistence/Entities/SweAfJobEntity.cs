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

    /// <summary>Port allocated on the provision host for this job's dedicated control plane container.</summary>
    public int? AllocatedPort { get; set; }

    /// <summary>HTTP base URL of this job's dedicated control plane, e.g. "http://192.168.1.10:8101".</summary>
    [MaxLength(500)]
    public string? ControlPlaneUrl { get; set; }

    /// <summary>Docker Compose project name for this job's control plane, e.g. "agentfield-42".</summary>
    [MaxLength(200)]
    public string? ComposeProjectName { get; set; }

    /// <summary>When true the job is hidden from the default builds list.</summary>
    public bool IsArchived { get; set; }
}
