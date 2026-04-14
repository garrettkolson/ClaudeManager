using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ClaudeManager.Hub.Services;

namespace ClaudeManager.Hub.Persistence.Entities;

[Table("LlmDeployments")]
public class LlmDeploymentEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>Unique human-readable slug.</summary>
    [MaxLength(100)]
    public string DeploymentId { get; set; } = default!;

    /// <summary>References GpuHostEntity.HostId.</summary>
    [MaxLength(100)]
    public string HostId { get; set; } = default!;

    /// <summary>HuggingFace model ID, e.g. "meta-llama/Llama-3.1-8B-Instruct".</summary>
    [MaxLength(500)]
    public string ModelId { get; set; } = default!;

    /// <summary>Comma-separated GPU indices, e.g. "0" or "0,1".</summary>
    [MaxLength(200)]
    public string GpuIndices { get; set; } = default!;

    /// <summary>Port exposed on the GPU host (mapped to container port 8000).</summary>
    public int HostPort { get; set; }

    /// <summary>Quantization mode: "none", "awq", or "gptq".</summary>
    [MaxLength(50)]
    public string Quantization { get; set; } = "none";

    /// <summary>Docker image tag: "latest" (stable) or "nightly" (cutting edge).</summary>
    [MaxLength(50)]
    public string ImageTag { get; set; } = "latest";

    /// <summary>Additional vLLM CLI arguments, e.g. "--max-model-len 4096 --dtype float16".</summary>
    [MaxLength(1000)]
    public string? ExtraArgs { get; set; }

    /// <summary>Use host networking (--network host) instead of port mapping (-p).</summary>
    public bool UseHostNetwork { get; set; } = false;

    /// <summary>Shared memory size passed to Docker (--shm-size), e.g. "16G". Null uses Docker default.</summary>
    [MaxLength(10)]
    public string? ShmSize { get; set; }

    /// <summary>Custom name for the served model in the OpenAI-compatible API (--served-model-name).</summary>
    [MaxLength(100)]
    public string? ServedModelName { get; set; }

    /// <summary>Fraction of GPU memory to reserve for the model (--gpu-memory-utilization), e.g. 0.88.</summary>
    public double? GpuMemoryUtilization { get; set; }

    /// <summary>Per-deployment HuggingFace token; overrides the global Hub secret when set.</summary>
    [MaxLength(500)]
    public string? HfTokenOverride { get; set; }

    /// <summary>
    /// The max_model_len to pass to vLLM. When null the vLLM default is used.
    /// Set from pre-deployment context estimation based on available VRAM.
    /// </summary>
    public int? MaxModelLen { get; set; }

    public LlmDeploymentStatus Status { get; set; }

    /// <summary>Docker container ID returned by "docker run -d".</summary>
    [MaxLength(100)]
    public string? ContainerId { get; set; }

    /// <summary>Last error message when Status = Error.</summary>
    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt  { get; set; }
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Number of auto-restarts since last manual start (reset on manual start).</summary>
    public int RestartCount { get; set; }

    /// <summary>When health was last verified by the health check service.</summary>
    public DateTimeOffset? LastHealthCheckAt { get; set; }
}
