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

    /// <summary>
    /// Hostname or IP address of the machine where the control plane will be deployed.
    /// Use "localhost" / "127.0.0.1" / "::1" to deploy on the local machine.
    /// </summary>
    [MaxLength(500)]
    public string? ProvisionHost { get; set; }

    /// <summary>
    /// SSH username for remote provisioning. Ignored when ProvisionHost is localhost.
    /// </summary>
    [MaxLength(100)]
    public string? SshUser { get; set; }

    /// <summary>
    /// Path to SSH private key for remote provisioning. Ignored when ProvisionHost is localhost.
    /// </summary>
    [MaxLength(500)]
    public string? SshKeyPath { get; set; }

    /// <summary>
    /// SSH password for remote provisioning (used when SshKeyPath is not set).
    /// Ignored when ProvisionHost is localhost.
    /// </summary>
    [MaxLength(500)]
    public string? SshPassword { get; set; }

    /// <summary>
    /// SSH port for remote provisioning. Ignored when ProvisionHost is localhost.
    /// </summary>
    public int SshPort { get; set; } = 22;

    /// <summary>
    /// Indicates whether sudo/root privileges are required to execute Docker commands
    /// on the provisioning host.
    /// </summary>
    public bool RequiresSudo { get; set; }

    /// <summary>
    /// Sudo password used for privilege escalation when RequiresSudo is true.
    /// This password is used with 'sudo -S' to authenticate Docker commands.
    /// </summary>
    [MaxLength(500)]
    public string? SudoPassword { get; set; }

    /// <summary>
    /// Anthropic API key injected into the SWE-AF agent container as ANTHROPIC_API_KEY.
    /// Required when Runtime is "claude_code".
    /// </summary>
    [MaxLength(500)]
    public string? AnthropicApiKey { get; set; }

    /// <summary>
    /// OpenRouter API key injected into the SWE-AF agent container as ANTHROPIC_API_KEY
    /// when Runtime is "openrouter".
    /// </summary>
    [MaxLength(500)]
    public string? OpenRouterApiKey { get; set; }

    /// <summary>
    /// OpenRouter endpoint URL injected as ANTHROPIC_BASE_URL when Runtime is "openrouter".
    /// Typically "https://openrouter.ai/api/v1".
    /// </summary>
    [MaxLength(500)]
    public string? OpenRouterEndpointUrl { get; set; }

    /// <summary>
    /// Path to the cloned SWE-AF repository on the provision host.
    /// Used by provisioning to run docker compose from the correct directory.
    /// Defaults to ~/swe-af when null.
    /// </summary>
    [MaxLength(500)]
    public string? SweAfRepoPath { get; set; }

    /// <summary>
    /// DeploymentId of the LlmDeploymentEntity whose host/port should be used as
    /// ANTHROPIC_BASE_URL when provisioning the SWE-AF agent container.
    /// When null, ANTHROPIC_BASE_URL is omitted from the .env file.
    /// </summary>
    [MaxLength(100)]
    public string? LlmDeploymentId { get; set; }

    /// <summary>
    /// YAML content written to docker-compose.override.yml in the SWE-AF repo directory
    /// on every provisioning run. Use this to declare named volumes (or bind mounts) so
    /// that build history and agent state survive docker compose down/up cycles.
    /// When null/empty, any existing override file is left untouched.
    /// </summary>
    public string? ComposeOverride { get; set; }

    /// <summary>Lowest port in the pool allocated for per-build control plane containers.</summary>
    public int PortRangeStart { get; set; } = 8100;

    /// <summary>Highest port in the pool (inclusive). Range size = max concurrent builds.</summary>
    public int PortRangeEnd { get; set; } = 8199;

    /// <summary>Docker image tag for the AgentField control plane, e.g. "latest". Written into .env as AGENTFIELD_IMAGE_TAG.</summary>
    [MaxLength(100)]
    public string? ControlPlaneImageTag { get; set; }

    [NotMapped]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
