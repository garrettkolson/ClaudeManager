namespace ClaudeManager.Hub.Services;

/// <summary>
/// Estimates the maximum feasible context length (max_model_len) for a vLLM deployment
/// given the model architecture, available GPU VRAM, and quantization.
/// </summary>
public static class MaxContextEstimator
{
    /// <param name="config">Model architecture config from HuggingFace.</param>
    /// <param name="totalVramGb">Total VRAM across all selected GPUs in GB.</param>
    /// <param name="paramsBillions">Model parameter count in billions (for weight VRAM estimate).</param>
    /// <param name="quantization">"none" (fp16), "awq", or "gptq".</param>
    /// <param name="gpuMemoryUtilization">Fraction of VRAM vLLM is allowed to use (default 0.90).</param>
    /// <returns>
    /// Recommended max_model_len to pass to vLLM, capped by the model's native maximum.
    /// Returns null if the model weights alone exceed available VRAM.
    /// </returns>
    public static long? Estimate(
        ModelArchConfig config,
        double totalVramGb,
        double paramsBillions,
        string quantization,
        double gpuMemoryUtilization = 0.90)
    {
        var modelWeightGb  = VramEstimator.EstimateGb(paramsBillions, quantization);
        var usableVramGb   = totalVramGb * gpuMemoryUtilization;
        var kvBudgetGb     = usableVramGb - modelWeightGb;

        if (kvBudgetGb <= 0) return null;

        // KV cache: 2 (K+V) × layers × kv_heads × head_dim × 2 bytes (fp16)
        var kvBytesPerToken = (long)2 * config.NumHiddenLayers * config.NumKvHeads * config.HeadDim * 2;
        var kvBudgetBytes   = (long)(kvBudgetGb * 1024 * 1024 * 1024);

        var maxFromVram = kvBudgetBytes / kvBytesPerToken;

        // Cap at model's native maximum context
        return Math.Min(maxFromVram, config.MaxPositionEmbeddings);
    }

    /// <summary>
    /// Returns a human-readable summary of the context estimate, e.g. "~32K tokens (native: 128K)".
    /// </summary>
    public static string FormatTokens(long tokens) =>
        tokens >= 1024
            ? $"~{tokens / 1024}K tokens"
            : $"~{tokens} tokens";
}
