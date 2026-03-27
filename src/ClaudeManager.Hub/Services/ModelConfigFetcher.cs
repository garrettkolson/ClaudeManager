using System.Text.Json;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Fetches and parses a HuggingFace model's config.json to extract
/// architectural parameters needed for KV cache estimation.
/// </summary>
public class ModelConfigFetcher
{
    private readonly HttpClient _http;
    private readonly ILogger<ModelConfigFetcher> _logger;

    public ModelConfigFetcher(HttpClient http, ILogger<ModelConfigFetcher> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>
    /// Downloads and parses config.json for the given model.
    /// </summary>
    /// <param name="modelId">HuggingFace model ID, e.g. "meta-llama/Llama-3.1-8B-Instruct".</param>
    /// <param name="hfToken">Optional HuggingFace token for private/gated models.</param>
    public async Task<(ModelArchConfig? Config, string? Error)> FetchAsync(
        string modelId, string? hfToken = null, CancellationToken ct = default)
    {
        var url = $"https://huggingface.co/{modelId}/resolve/main/config.json";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(hfToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", hfToken);

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode} fetching config.json for '{modelId}'.");

            var json = await response.Content.ReadAsStringAsync(ct);
            var config = ParseConfig(modelId, json);

            if (config is null)
                return (null, $"Could not parse required fields from config.json for '{modelId}'.");

            return (config, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "Request cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch config.json for {ModelId}", modelId);
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Parses a HuggingFace config.json string into a <see cref="ModelArchConfig"/>.
    /// Returns null if required fields are missing or cannot be parsed.
    /// </summary>
    internal static ModelArchConfig? ParseConfig(string modelId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("num_hidden_layers", out var layersProp) ||
                !layersProp.TryGetInt32(out var numHiddenLayers))
                return null;

            if (!root.TryGetProperty("num_attention_heads", out var headsProp) ||
                !headsProp.TryGetInt32(out var numAttentionHeads) ||
                numAttentionHeads <= 0)
                return null;

            if (!root.TryGetProperty("hidden_size", out var hiddenSizeProp) ||
                !hiddenSizeProp.TryGetInt32(out var hiddenSize))
                return null;

            if (!root.TryGetProperty("max_position_embeddings", out var maxPosProp) ||
                !maxPosProp.TryGetInt64(out var maxPositionEmbeddings))
                return null;

            // GQA/MQA models have num_key_value_heads < num_attention_heads; fallback to full heads
            var numKvHeads = numAttentionHeads;
            if (root.TryGetProperty("num_key_value_heads", out var kvHeadsProp) &&
                kvHeadsProp.TryGetInt32(out var parsedKvHeads) &&
                parsedKvHeads > 0)
            {
                numKvHeads = parsedKvHeads;
            }

            var headDim = hiddenSize / numAttentionHeads;

            return new ModelArchConfig(
                ModelId:               modelId,
                NumHiddenLayers:       numHiddenLayers,
                NumKvHeads:            numKvHeads,
                HeadDim:               headDim,
                MaxPositionEmbeddings: maxPositionEmbeddings);
        }
        catch
        {
            return null;
        }
    }
}
