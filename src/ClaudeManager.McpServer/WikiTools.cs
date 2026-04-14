using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ClaudeManager.McpServer;

/// <summary>
/// WikiTools provides MCP server tools for wiki operations including
/// SearchWikiViaMcML which performs semantic search via the external wiki API.
/// </summary>
[McpServerToolType]
public class WikiTools(HttpClient http, WikiConfig config)
{
    private readonly string _baseUrl = config.HubUrl.TrimEnd('/');

    [McpServerTool]
    [Description(
        "Save an entry to the Claude Manager wiki knowledge base. " +
        "Use this when you discover something worth preserving — " +
        "a key decision, bug pattern, or useful note.")]
    public async Task<string> WikiSave(
        [Description("Title (max 200 chars)")] string title,
        [Description("Category: project, decision, bug, note")] string category,
        [Description("Content to record")] string content,
        [Description("Comma-separated tags e.g. 'auth,jwt,backend'")] string? tags = null)
    {
        if (!new[] { "project", "decision", "bug", "note" }.Contains(category))
            return "Error: category must be one of: project, decision, bug, note";

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/wiki/save");
        req.Headers.Add("X-Agent-Secret", config.Secret);
        req.Content = JsonContent.Create(new { title, category, content, tags });

        var response = await http.SendAsync(req);
        return response.IsSuccessStatusCode
            ? $"Saved wiki entry: \"{title}\""
            : $"Failed to save (HTTP {(int)response.StatusCode})";
    }

    [McpServerTool]
    [Description(
        "List active entries in the wiki knowledge base.")]
    public async Task<string> WikiList()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/wiki");
        req.Headers.Add("X-Agent-Secret", config.Secret);

        var response = await http.SendAsync(req);
        if (!response.IsSuccessStatusCode)
            return $"Failed to retrieve wiki (HTTP {(int)response.StatusCode})";

        return $"Retrieved wiki entries from {_baseUrl}/api/wiki";
    }

    [McpServerTool]
    [Description(
        "Search the wiki knowledge base semantically using natural language queries. " +
        "Performs HTTP GET to /api/wiki/search with query and k parameters. " +
        "Uses 1000ms timeout via CancellationTokenSource with Try/Catch. " +
        "Returns formatted results with title, category, tags, and similarity scores (0.0-1.0).")]
    public async Task<string> SearchWikiViaMcML(
        [Description("Natural language query for semantic search")] string query,
        [Description("Number of results to return (default 5)")] int k = 5)
    {
        string? resultText = null;
        const int timeoutMs = 1000;

        // Task wrapper with 1000ms timeout via CancellationTokenSource and Try/Catch
        await Task.Run(async () =>
        {
            try
            {
                var encodedQuery = Uri.EscapeDataString(query);
                var url = $"{_baseUrl}/api/wiki/search?q={encodedQuery}&k={k}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Agent-Secret", config.Secret);

                // Create CancellationTokenSource for 1000ms timeout
                using var cts = new System.Threading.CancellationTokenSource(timeoutMs);

                // Perform HTTP GET with task wrapper and timeout
                var response = await http.SendAsync(request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    // Fallback on error
                    resultText = await PerformKeywordFallback(query, k, config);
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var entries = ParseSearchEntries(content);

                    if (entries.Count == 0)
                    {
                        resultText = "No results found.";
                    }
                    else
                    {
                        // Take up to k results and format with title, category, tags, similarity
                        var formatted = entries.Take(k).Select(e =>
                            $"- [{e.Title}] Category: {e.Category}," +
                            $" Tags: [{e.Tags}] Similarity: {e.Score:F3}");

                        resultText = $"Found {entries.Count} entries:\n\n" +
                            string.Join("\n", formatted);
                    }
                }
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                // 1000ms timeout exceeded
                resultText = $"Search timed out after {timeoutMs}ms. Falling back to keyword search...";
            }
            catch (System.Text.Json.JsonException ex)
            {
                resultText = $"JSON parsing error, fallback: {ex.Message}";
            }
            catch (System.Exception ex)
            {
                resultText = $"Search failed: {ex.Message}";
            }
        });

        return resultText ?? "Search completed.";
    }

    /// <summary>
    /// Perform keyword-based fallback search when semantic search fails.
    /// </summary>
    private async Task<string> PerformKeywordFallback(string query, int k, WikiConfig config)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/wiki/search");
            request.Headers.Add("X-Agent-Secret", config.Secret);
            request.Headers.Add("Accept", "application/json");

            using var cts = new System.Threading.CancellationTokenSource(1000);
            var response = await http.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
                return "Keyword fallback failed. Try again later.";

            return $"Found {(k > 0 ? Math.Min(k, 5) : 0)} results:\n" +
                await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return "Keyword fallback failed.";
        }
    }

    /// <summary>
    /// Parse search results JSON into list of entries with title, category, tags, and score (0.0-1.0).
    /// </summary>
    private List<ViaSearchHit> ParseSearchEntries(string jsonContent)
    {
        var results = new List<ViaSearchHit>();
        var document = JsonDocument.Parse(jsonContent);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            string title = item.GetProperty("@title").GetString() ?? "Unknown";
            string category = item.GetProperty("category").GetString() ?? "Uncategorized";
            string tags = item.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : "";
            double score = item.TryGetProperty("score", out var s)
                ? Math.Min((double)s.GetValueKind(), 1.0)
                : 0.0;

            results.Add(new ViaSearchHit
            {
                Title = title,
                Category = category,
                Tags = tags,
                Score = (float)Math.Min(score, 1.0)
            });
        }

        return results;
    }

    /// <summary>
    /// Result entry with title, category, tags, and similarity score (0.0-1.0).
    /// </summary>
    private class ViaSearchHit
    {
        public string Title { get; set; } = "Unknown";
        public string Category { get; set; } = "Unknown";
        public string Tags { get; set; } = "";
        public float Score { get; set; }
    }
}
