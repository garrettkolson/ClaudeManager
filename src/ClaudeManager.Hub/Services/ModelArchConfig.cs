namespace ClaudeManager.Hub.Services;

/// <summary>
/// Architectural parameters extracted from a HuggingFace model's config.json.
/// Used to estimate KV cache size and maximum feasible context length.
/// </summary>
/// <param name="ModelId">HuggingFace model identifier.</param>
/// <param name="NumHiddenLayers">Number of transformer layers.</param>
/// <param name="NumKvHeads">Number of KV attention heads (GQA/MQA models have fewer than Q heads).</param>
/// <param name="HeadDim">Dimension of each attention head (hidden_size / num_attention_heads).</param>
/// <param name="MaxPositionEmbeddings">Model's native maximum context length.</param>
public record ModelArchConfig(
    string ModelId,
    int    NumHiddenLayers,
    int    NumKvHeads,
    int    HeadDim,
    long   MaxPositionEmbeddings);
