namespace ClaudeManager.Hub.Models;

/// <summary>
/// Proxy configuration returned to agents at startup.
/// </summary>
public record LlmProxyConfig(
    /// <summary>Base URL for the proxy (e.g. "http://gpu-host:8080").</summary>
    string ProxyUrl,

    /// <summary>
    /// List of available models with their path slugs.
    /// Agents construct full endpoints as $"{ProxyUrl}/{ModelSlug}/v1".
    /// </summary>
    IReadOnlyList<string> ModelSlugs);
