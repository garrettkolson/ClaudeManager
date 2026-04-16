using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public string? Assignee { get; set; }     // display name or email
    public List<string> Labels { get; set; } = [];
    public double? StoryPoints { get; set; }
    public string Url { get; set; } = "";

    public JiraIssue Clone() => new()
    {
        Key = Key,
        Summary = Summary,
        Description = Description,
        Status = Status is not null ? JsonSerializer.SerializeToElement(Status) : null,
        Issuetype = Issuetype,
        Priority = Priority,
        Assignee = Assignee,
        Labels = Labels.ToList(),
        StoryPoints = StoryPoints,
        Url = Url
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

// ── Search Result DTO ───────────────────────────────────────────────────────

public class JiraSearchResult
{
    public int Total { get; set; }
    public int MaxResults { get; set; }
    public int StartAt { get; set; }
    public List<JiraIssue> Issues { get; set; } = [];
}

// ── Helper: ADF to Plain Text ───────────────────────────────────────────────

/// <summary>
/// Converts Atlassian Document Format (ADF) JSON to plain text.
/// Handles both plain strings and ADF arrays with block/inline nodes.
/// </summary>
public static class JiraDtosHelpers
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string ToPlainText(object? atlassianDoc)
    {
        if (atlassianDoc is null) return "";
        if (atlassianDoc is string s) return s;
        if (atlassianDoc is JsonElement element)
        {
            var json = element.GetRawText();
            return TryParseAdf(json);
        }

        // Try parsing as ADF JSON string
        var str = atlassianDoc.ToString() ?? "";
        try
        {
            var element2 = JsonSerializer.Deserialize<JsonElement>(str);
            return TryParseAdf(str);
        }
        catch
        {
            // Fallback: return original
            return str;
        }
    }

    private static string TryParseAdf(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<List<AdfBlock>>(json, _jsonOptions);
            if (doc is null) return json;

            var sb = new StringBuilder();
            foreach (var block in doc)
            {
                if (block.Text is not null) sb.AppendLine(block.Text);
                else if (block.Content is not null)
                {
                    foreach (var inline in block.Content)
                    {
                        if (inline.Text is not null) sb.Append(inline.Text);
                    }
                }
                else if (block.ContentMap?.Text is not null)
                {
                    foreach (var inline in block.ContentMap.Text)
                    {
                        if (inline.Text is not null) sb.Append(inline.Text);
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }
        catch
        {
            // Fallback: return original
            return json;
        }
    }
}

// ── ADF Internal Classes ──────────────────────────────────────────────────────

public class AdfBlock
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public List<AdfInline>? Content { get; set; }
    public AdfContentMap? ContentMap { get; set; }
}

public class AdfInline
{
    public string? Type { get; set; }
    public string? Text { get; set; }
}

public class AdfContentMap
{
    public List<AdfInline>? Text { get; set; }
}
