namespace ClaudeManager.Hub.Services;

/// <summary>
/// Raw information extracted from <c>docker inspect</c> for a running LLM container.
/// Supports both vLLM and llama.cpp/llama-server images.
/// </summary>
public record RawLlmContainerInfo(
    string        ContainerId,
    string        ImageTag,
    string?       ModelId,
    int?          HostPort,
    string?       GpuIndices,
    string?       Quantization,
    int?          MaxModelLen,
    int?          NggpuLayers,
    DeploymentType DeploymentType
);

/// <summary>
/// An LLM container found on a GPU host, enriched with whether it is already
/// tracked in the ClaudeManager database as a deployment.
/// </summary>
public record DetectedLlmContainer(
    string        ContainerId,
    string        ImageTag,
    string?       ModelId,
    int?          HostPort,
    string?       GpuIndices,
    string?       Quantization,
    int?          MaxModelLen,
    int?          NggpuLayers,
    DeploymentType? DeploymentType,
    bool          AlreadyTracked,
    long?         ExistingDeploymentDbId,
    string?       ExistingDeploymentId
);
