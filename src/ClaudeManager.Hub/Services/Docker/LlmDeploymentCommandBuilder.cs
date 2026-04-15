namespace ClaudeManager.Hub.Services.Docker;

/// <summary>
/// Configuration for LLM deployment (vLLM or llama.cpp).
/// </summary>
public record LlmDeploymentConfig(
    DeploymentType DeploymentType,
    string ImageTag,
    string ModelId,
    int HostPort,
    string? GpuIndices = null,
    string? Quantization = null,
    int? MaxModelLen = null,
    int? TensorParallelSize = null,
    string? ExtraArgs = null,
    string? HfToken = null,
    string? ServedModelName = null,
    double? GpuMemoryUtilization = null,
    int? NggpuLayers = null,
    bool UseHostNetwork = false
);

/// <summary>
/// Builder for LLM server container Docker commands.
/// Supports both vLLM and llama.cpp/llama-server deployment types.
/// </summary>
public class LlmDeploymentCommandBuilder
{
    private readonly DockerCommandBuilder _baseBuilder;
    private LlmDeploymentConfig? _config;

    public LlmDeploymentCommandBuilder()
    {
        _baseBuilder = new DockerCommandBuilder();
    }

    /// <summary>
    /// Sets the container name.
    /// </summary>
    public LlmDeploymentCommandBuilder WithContainerName(string name)
    {
        _baseBuilder.WithContainerName(name);
        return this;
    }

    /// <summary>
    /// Configures GPU device allocation. Pass "all" to allocate all GPUs (--gpus all),
    /// or a comma-separated list of indices (--gpus '"device=0,1"').
    /// </summary>
    public LlmDeploymentCommandBuilder WithGpus(string gpuIndices)
    {
        if (gpuIndices.Equals("all", StringComparison.OrdinalIgnoreCase))
            _baseBuilder.WithFlag("--gpus all");
        else
            _baseBuilder.WithGpus($"device={gpuIndices}");
        return this;
    }

    /// <summary>
    /// Uses the NVIDIA runtime for GPU access.
    /// </summary>
    public LlmDeploymentCommandBuilder WithNvidiaRuntime()
    {
        _baseBuilder.WithNvidiaRuntime();
        return this;
    }

    /// <summary>
    /// Shares the host IPC namespace for multi-GPU communication.
    /// </summary>
    public LlmDeploymentCommandBuilder WithHostIPC()
    {
        _baseBuilder.WithHostIPC();
        return this;
    }

    /// <summary>
    /// Maps the host port to vLLM's container port (8000).
    /// </summary>
    public LlmDeploymentCommandBuilder WithHostPort(int hostPort)
    {
        _baseBuilder.WithPortMapping(hostPort, "8000");
        return this;
    }

    /// <summary>
    /// Uses host networking (--network host). vLLM binds directly to the host network stack;
    /// no port mapping is added. Use WithModel's hostPort parameter to set the --port arg.
    /// </summary>
    public LlmDeploymentCommandBuilder WithHostNetwork()
    {
        _baseBuilder.WithHostNetwork();
        return this;
    }

    /// <summary>
    /// Sets the shared memory size (--shm-size), e.g. "16G".
    /// </summary>
    public LlmDeploymentCommandBuilder WithShmSize(string size)
    {
        _baseBuilder.WithShmSize(size);
        return this;
    }

    /// <summary>
    /// Mounts HuggingFace cache directory for model caching.
    /// Uses a Linux-compatible path derived from the remote SSH user.
    /// </summary>
    public LlmDeploymentCommandBuilder WithHfCacheVolume(string? remoteUser = null)
    {
        var homeDir = string.IsNullOrEmpty(remoteUser) || remoteUser == "root"
            ? "/root"
            : $"/home/{remoteUser}";
        var cacheDir = $"{homeDir}/.cache/huggingface";
        _baseBuilder.WithVolume(cacheDir, "/root/.cache/huggingface");
        return this;
    }

    /// <summary>
    /// Sets the HuggingFace Hub token as an environment variable.
    /// </summary>
    public LlmDeploymentCommandBuilder WithHfToken(string token)
    {
        _baseBuilder.WithEnvironmentVariable("HUGGING_FACE_HUB_TOKEN", token);
        return this;
    }

    /// <summary>
    /// Sets the restart policy.
    /// </summary>
    public LlmDeploymentCommandBuilder WithRestartPolicy(string policy)
    {
        _baseBuilder.WithRestartPolicy(policy);
        return this;
    }

    /// <summary>
    /// Sets the vLLM image tag.
    /// </summary>
    /// <summary>
    /// Sets the deployment type (vLLM or llama.cpp).
    /// </summary>
    public LlmDeploymentCommandBuilder WithDeploymentType(DeploymentType type)
    {
        _config = (_config ?? new LlmDeploymentConfig(
            DeploymentType: type,
            ImageTag: string.Empty,
            ModelId: string.Empty,
            HostPort: 0)) with
        {
            DeploymentType = type,
        };
        return this;
    }

    public LlmDeploymentCommandBuilder WithImageTag(string tag)
    {
        var imagePrefix = _config?.DeploymentType == DeploymentType.Llamacpp
            ? "ghcr.io/ggerganov/llama-server"
            : "vllm/vllm-openai";
        _baseBuilder.WithImage($"{imagePrefix}:{tag}");
        return this;
    }

    /// <summary>
    /// Configures llama.cpp GPU layer count (--n-gpu-layers).
    /// </summary>
    public LlmDeploymentCommandBuilder WithNggpuLayers(int? layers)
    {
        _config ??= new LlmDeploymentConfig(
            DeploymentType: DeploymentType.Vllm,
            ImageTag: string.Empty,
            ModelId: string.Empty,
            HostPort: 0);
        _config = _config with { NggpuLayers = layers };
        return this;
    }

    /// <summary>
    /// Configures model parameters for the deployment.
    /// </summary>
    public LlmDeploymentCommandBuilder WithModel(string modelId, string? quantization = null, int? maxModelLen = null)
    {
        _config = (_config ?? new LlmDeploymentConfig(
            DeploymentType: DeploymentType.Vllm,
            ImageTag: string.Empty,
            ModelId: modelId,
            HostPort: 0)) with
        {
            ModelId = modelId,
            Quantization = quantization ?? string.Empty,
            MaxModelLen = maxModelLen,
        };
        return this;
    }

    /// <summary>
    /// Sets the served model name for the OpenAI-compatible API (--served-model-name).
    /// </summary>
    /// <summary>
    /// Sets the served model name for the OpenAI-compatible API (--served-model-name).
    /// vLLM only; llama.cpp uses --model alias.
    /// </summary>
    public LlmDeploymentCommandBuilder WithServedModelName(string name)
    {
        _config ??= new LlmDeploymentConfig(
            DeploymentType: DeploymentType.Vllm,
            ImageTag: string.Empty,
            ModelId: string.Empty,
            HostPort: 0);
        _config = _config with { ServedModelName = name };
        return this;
    }

    /// <summary>
    /// Sets the GPU memory utilization fraction (--gpu-memory-utilization), e.g. 0.88.
    /// vLLM only; llama.cpp uses --n-gpu-layers.
    /// </summary>
    public LlmDeploymentCommandBuilder WithGpuMemoryUtilization(double utilization)
    {
        _config ??= new LlmDeploymentConfig(
            DeploymentType: DeploymentType.Vllm,
            ImageTag: string.Empty,
            ModelId: string.Empty,
            HostPort: 0);
        _config = _config with { GpuMemoryUtilization = utilization };
        return this;
    }

    /// <summary>
    /// Sets the host port, stored on the config for use as the vLLM --port arg when using host networking.
    /// </summary>
    public LlmDeploymentCommandBuilder WithVllmPort(int port)
    {
        _config ??= new LlmDeploymentConfig(ImageTag: string.Empty, ModelId: string.Empty, HostPort: 0);
        _config = _config with { HostPort = port, UseHostNetwork = true };
        return this;
    }

    /// <summary>
    /// Sets the tensor parallel size for multi-GPU deployment.
    /// vLLM only; llama.cpp does not support tensor parallelism.
    /// </summary>
    public LlmDeploymentCommandBuilder WithTensorParallelSize(int size)
    {
        _config ??= new LlmDeploymentConfig(
            DeploymentType: DeploymentType.Vllm,
            ImageTag: string.Empty,
            ModelId: string.Empty,
            HostPort: 0,
            GpuIndices: null,
            Quantization: string.Empty,
            MaxModelLen: null,
            TensorParallelSize: size,
            ExtraArgs: null,
            HfToken: null
        );
        _config = _config with { TensorParallelSize = size };
        return this;
    }

    /// <summary>
    /// Adds extra deployment command arguments.
    /// Arguments are passed directly to the underlying server (vLLM or llama-server).
    /// </summary>
    public LlmDeploymentCommandBuilder WithExtraArgs(string extraArgs)
    {
        _config ??= new LlmDeploymentConfig(
            DeploymentType: DeploymentType.Vllm,
            ImageTag: string.Empty,
            ModelId: string.Empty,
            HostPort: 0,
            GpuIndices: null,
            Quantization: string.Empty,
            MaxModelLen: null,
            TensorParallelSize: null,
            ExtraArgs: extraArgs,
            HfToken: null
        );
        _config = _config with { ExtraArgs = extraArgs };
        return this;
    }

    /// <summary>
    /// Requires sudo privileges for execution.
    /// </summary>
    public LlmDeploymentCommandBuilder RequiresSudo(string? sudoPassword = null)
    {
        _baseBuilder.RequiresSudo(sudoPassword);
        return this;
    }

    /// <summary>
    /// Builds the DockerCommand for the LLM deployment.
    /// Generates type-specific arguments for vLLM or llama.cpp.
    /// </summary>
    public DockerCommand Build()
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Model configuration must be set before building.");
        }

        var args = BuildCommandArgs(_config);
        var baseCommand = _baseBuilder
            .WithInteractiveDetached()
            .WithCommand(args)
            .Build();

        return baseCommand with
        {
            RequiresSudo = baseCommand.RequiresSudo,
            SudoPassword = baseCommand.SudoPassword
        };
    }

    private string[] BuildCommandArgs(LlmDeploymentConfig config)
    {
        var args = new List<string>();

        args.Add("--host");
        args.Add("0.0.0.0");
        args.Add("--port");
        args.Add(config.UseHostNetwork && config.HostPort > 0 ? config.HostPort.ToString() : "8000");

        // llama.cpp uses --model for the GGUF file path
        // vLLM uses --model for the model ID
        args.Add("--model");
        args.Add(config.ModelId);

        // llama.cpp specific
        if (config.DeploymentType == DeploymentType.Llamacpp)
        {
            if (config.NggpuLayers.HasValue)
            {
                args.Add("--n-gpu-layers");
                args.Add(config.NggpuLayers.Value.ToString());
            }
        }
        else
        {
            // vLLM specific
            if (config.TensorParallelSize.HasValue)
            {
                args.Add("--tensor-parallel-size");
                args.Add(config.TensorParallelSize.Value.ToString());
            }

            if (!string.IsNullOrEmpty(config.Quantization) && config.Quantization != "none")
            {
                args.Add("--quantization");
                args.Add(config.Quantization);
            }

            if (config.MaxModelLen.HasValue)
            {
                args.Add("--max-model-len");
                args.Add(config.MaxModelLen.Value.ToString());
            }

            if (config.GpuMemoryUtilization.HasValue)
            {
                args.Add("--gpu-memory-utilization");
                args.Add(config.GpuMemoryUtilization.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(config.ServedModelName))
            {
                args.Add("--served-model-name");
                args.Add(config.ServedModelName);
            }
        }

        // Extra args for both types
        if (!string.IsNullOrEmpty(config.ExtraArgs))
        {
            var extra = config.ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            args.AddRange(extra);
        }

        return [.. args];
    }
}
