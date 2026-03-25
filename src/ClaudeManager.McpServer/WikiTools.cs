using System.ComponentModel;
using System.Net.Http.Json;
using ModelContextProtocol.Server;

namespace ClaudeManager.McpServer;

[McpServerToolType]
public class WikiTools(HttpClient http, WikiConfig config)
{
    private readonly string _base = config.HubUrl.TrimEnd('/');

    [McpServerTool]
    [Description(
        "Save an entry to the Claude Manager wiki knowledge base. " +
        "Use this when you discover something worth preserving across sessions — " +
        "a key decision, a bug pattern, project context, or a useful note. " +
        "Creates a new entry or updates an existing one with the same title.")]
    public async Task<string> WikiSave(
        [Description("Short descriptive title (max 200 chars)")] string title,
        [Description("Category — one of: project, decision, bug, note")] string category,
        [Description("Content to record")] string content,
        [Description("Optional comma-separated tags, e.g. 'auth,jwt,backend'")] string? tags = null)
    {
        if (!new[] { "project", "decision", "bug", "note" }.Contains(category))
            return "Error: category must be one of: project, decision, bug, note";

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/wiki/save");
        req.Headers.Add("X-Agent-Secret", config.Secret);
        req.Content = JsonContent.Create(new { title, category, content, tags });

        var response = await http.SendAsync(req);
        return response.IsSuccessStatusCode
            ? $"Saved wiki entry: \"{title}\""
            : $"Failed to save wiki entry (HTTP {(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}";
    }

    [McpServerTool]
    [Description(
        "List active entries in the Claude Manager wiki knowledge base. " +
        "Use this to check what is already recorded before saving a new entry.")]
    public async Task<string> WikiList()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/wiki");
        req.Headers.Add("X-Agent-Secret", config.Secret);

        var response = await http.SendAsync(req);
        if (!response.IsSuccessStatusCode)
            return $"Failed to retrieve wiki (HTTP {(int)response.StatusCode})";

        var entries = await response.Content.ReadFromJsonAsync<WikiEntryDto[]>();
        if (entries is null || entries.Length == 0)
            return "Wiki is empty.";

        var lines = entries
            .GroupBy(e => e.Category)
            .SelectMany(g =>
                g.Select((e, i) => i == 0
                    ? $"[{g.Key}] {e.Title}{(e.Tags is not null ? $" ({e.Tags})" : "")}"
                    : $"         {e.Title}{(e.Tags is not null ? $" ({e.Tags})" : "")}"))
            .ToList();

        return string.Join("\n", lines);
    }

    private record WikiEntryDto(long Id, string Title, string Category, string? Tags);
}
