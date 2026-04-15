namespace ClaudeManager.Hub.Services;

/// <summary>
/// Type of LLM server deployment.
/// </summary>
public enum DeploymentType
{
    /// <summary>vLLM with CUDA - supports tensor parallelism, Awq/GPTQ quantization</summary>
    Vllm,

    /// <summary>llama.cpp/llama-server - uses GGUF models, GPU layers via --n-gpu-layers</summary>
    Llamacpp
}
