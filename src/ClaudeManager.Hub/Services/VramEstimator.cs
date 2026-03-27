namespace ClaudeManager.Hub.Services;

/// <summary>
/// Rough VRAM requirement estimates based on model parameter count and quantization.
/// Formula: parameters × bytes-per-param × 1.25 overhead factor.
/// </summary>
public static class VramEstimator
{
    /// <summary>
    /// Returns estimated VRAM in GB for a single GPU.
    /// </summary>
    /// <param name="paramsBillions">Model size in billions of parameters (e.g. 7.0 for a 7B model).</param>
    /// <param name="quantization">"none" (fp16), "awq", or "gptq".</param>
    public static double EstimateGb(double paramsBillions, string quantization)
    {
        // GB per billion parameters × 1.25 activation/KV-cache overhead.
        // fp16: ~2 GB/B params (standard community heuristic, e.g. 7B ≈ 14 GB base + 25% = ~17.5 GB)
        // awq/gptq: ~0.5 GB/B params (~4-bit quantization)
        var gbPerBillion = quantization switch
        {
            "awq" or "gptq" => 0.5,
            _               => 2.0,
        };

        return paramsBillions * gbPerBillion * 1.25;
    }

    /// <summary>Returns a human-readable estimate string, e.g. "~8.8 GB per GPU".</summary>
    public static string? FormatEstimate(double? paramsBillions, string quantization)
    {
        if (paramsBillions is null or <= 0) return null;
        var gb = EstimateGb(paramsBillions.Value, quantization);
        return $"~{gb:F1} GB per GPU";
    }
}
