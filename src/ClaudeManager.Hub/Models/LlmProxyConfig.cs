namespace ClaudeManager.Hub.Models;

/// <summary>
/// Proxy configuration returned to agents at startup.
/// </summary>
public record LlmProxyConfig(
    /// <summary>Base URL for the proxy (e.g. "http://gpu-host:8080").</summary>
    string ProxyUrl);
