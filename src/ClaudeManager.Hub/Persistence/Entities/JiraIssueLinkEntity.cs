using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeManager.Hub.Persistence.Entities;

/// <summary>
/// Tracks the relationship between Jira issues and local builds/sessions.
/// Only link records are persisted - no full Jira mirroring.
/// </summary>
public enum LinkType
{
    SweAfBuild = 0,
    AgentSession = 1
}

[Table("JiraIssueLinks")]
public class JiraIssueLinkEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(50)]
    [Index]
    public string IssueKey { get; set; } = "";

    [MaxLength(4000)]
    public string IssueSummary { get; set; } = "";

    public LinkType LinkType { get; set; }

    public long? SweAfJobId { get; set; }

    public string? SessionId { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    public DateTimeOffset? ReviewTransitionedAt { get; set; }

    // Navigation properties (optional, used for queries)
    public SweAfJobEntity? SweAfJob { get; set; }
    public ClaudeSessionEntity? ClaudeSession { get; set; }
}
