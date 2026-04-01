namespace ClaudeManager.Hub.Services;

/// <summary>
/// Raw information extracted from <c>docker inspect</c> for a running container.
/// </summary>
public record RawVllmContainerInfo(
    string  ContainerId,
    string  ImageTag,
    string? ModelId,
    int?    HostPort,
    string? GpuIndices,
    string? Quantization,
    int?    MaxModelLen
);

/// <summary>
/// A vLLM container found on a GPU host, enriched with whether it is already
/// tracked in the ClaudeManager database as a deployment.
/// </summary>
public record DetectedVllmContainer(
    string  ContainerId,
    string  ImageTag,
    string? ModelId,
    int?    HostPort,
    string? GpuIndices,
    string? Quantization,
    int?    MaxModelLen,
    bool    AlreadyTracked,
    long?   ExistingDeploymentDbId,
    string? ExistingDeploymentId
);
