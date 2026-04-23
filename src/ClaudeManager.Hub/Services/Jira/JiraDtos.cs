using System.Text;
using System.Text.Json;

namespace ClaudeManager.Hub.Services.Jira;

// ── Issue DTO ──────────────────────────────────────────────────────────────

public class JiraIssue
{
    public string Key { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Description { get; set; }  // ADF JSON or plain text
    public object? Status { get; set; }       // JiraStatus or string
    public string Issuetype { get; set; } = "";
    public string Priority { get; set; } = "";
    public string? Assignee { get; set; }
    public List<string> Labels { get; set; } = [];
    public double? StoryPoints { get; set; }
    public string Url { get; set; } = "";

    public JiraIssue Clone() => new()
    {
        Key         = Key,
        Summary     = Summary,
        Description = Description,
        Status      = Status is not null ? JsonSerializer.SerializeToElement(Status) : null,
        Issuetype   = Issuetype,
        Priority    = Priority,
        Assignee    = Assignee,
        Labels      = Labels.ToList(),
        StoryPoints = StoryPoints,
        Url         = Url,
    };
}

// ── Status DTO ──────────────────────────────────────────────────────────────

public class JiraStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string StatusCategory { get; set; } = "";  // "To Do", "In Progress", or "Done"
}

// ── Transition DTO ──────────────────────────────────────────────────────────

public class JiraTransition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JiraStatus To { get; set; } = null!;
}

// ── User DTO ────────────────────────────────────────────────────────────────

public record JiraUser(string Name, string Username);

// ── Search Result DTO ───────────────────────────────────────────────────────

public class JiraSearchResult
{
    public int Total { get; set; }
    public int MaxResults { get; set; }
    public int StartAt { get; set; }
    public List<JiraIssue> Issues { get; set; } = [];
}

// ── Helper: ADF → plain text ────────────────────────────────────────────────

public static class JiraDtosHelpers
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string ToPlainText(object? atlassianDoc)
    {
        if (atlassianDoc is null) return "";

        string json;
        if (atlassianDoc is string s)
        {
            s = s.Trim();
            if (!s.StartsWith('{') && !s.StartsWith('[')) return s;
            json = s;
        }
        else if (atlassianDoc is JsonElement el)
        {
            json = el.GetRawText();
        }
        else
        {
            json = atlassianDoc.ToString() ?? "";
        }

        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);
            return RenderNode(root).TrimEnd();
        }
        catch
        {
            return json;
        }
    }

    private static string RenderNode(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            if (node.GetArrayLength() == 0) return "";

            // Non-ADF array: check if any element has a "type" property
            bool hasType = false;
            foreach (var child in node.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty("type", out _))
                {
                    hasType = true;
                    break;
                }
            }
            if (!hasType) return node.GetRawText();

            var sb = new StringBuilder();
            foreach (var child in node.EnumerateArray())
                sb.Append(RenderNode(child));
            return sb.ToString();
        }
        if (node.ValueKind != JsonValueKind.Object) return "";

        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;

        return type switch
        {
            "doc"         => RenderChildren(node),
            "paragraph"   => RenderParagraph(node) + "\n",
            "heading"     => RenderParagraph(node) + "\n",
            "blockquote"  => RenderChildren(node),
            "codeBlock"   => RenderChildren(node) + "\n",
            "bulletList"  => RenderList(node, "• "),
            "orderedList" => RenderOrderedList(node),
            "listItem"    => RenderChildren(node),
            "rule"        => "---\n",
            "hardBreak"   => "\n",
            "text"        => node.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
            "mention"     => node.TryGetProperty("attrs", out var ma) && ma.TryGetProperty("text",  out var mt) ? mt.GetString() ?? "" : "@mention",
            "emoji"       => node.TryGetProperty("attrs", out var ea) && ea.TryGetProperty("text",  out var et) ? et.GetString() ?? "" : "",
            "inlineCard"  => node.TryGetProperty("attrs", out var ca) && ca.TryGetProperty("url",   out var cu) ? cu.GetString() ?? "" : "",
            _             => RenderChildren(node),
        };
    }

    private static string RenderChildren(JsonElement node)
    {
        var sb = new StringBuilder();

        if (node.TryGetProperty("content", out var content))
            foreach (var child in content.EnumerateArray())
                sb.Append(RenderNode(child));

        if (node.TryGetProperty("contentMap", out var contentMap))
            foreach (var mapValue in contentMap.EnumerateObject())
            {
                if (mapValue.Value.ValueKind == JsonValueKind.Array)
                    foreach (var child in mapValue.Value.EnumerateArray())
                        sb.Append(RenderNode(child));
            }

        return sb.ToString();
    }

    private static string RenderParagraph(JsonElement node)
    {
        // ADF paragraphs can have direct "text" property or "content" array
        if (node.TryGetProperty("text", out var directText) && directText.ValueKind == JsonValueKind.String)
            return directText.GetString() ?? "";
        return RenderChildren(node);
    }

    private static string RenderList(JsonElement node, string bullet)
    {
        if (!node.TryGetProperty("content", out var items)) return "";
        var sb = new StringBuilder();
        foreach (var item in items.EnumerateArray())
        {
            sb.Append(bullet);
            sb.Append(RenderNode(item).TrimEnd('\n', ' '));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string RenderOrderedList(JsonElement node)
    {
        if (!node.TryGetProperty("content", out var items)) return "";
        var sb = new StringBuilder();
        var i = 1;
        foreach (var item in items.EnumerateArray())
        {
            sb.Append($"{i++}. ");
            sb.Append(RenderNode(item).TrimEnd('\n', ' '));
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
