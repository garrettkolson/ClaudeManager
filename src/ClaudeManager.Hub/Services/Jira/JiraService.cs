using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClaudeManager.Hub.Services.Jira;

public class JiraService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly JiraConfigService _configSvc;
    private readonly ILogger<JiraService> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public JiraService(
        IHttpClientFactory httpFactory,
        JiraConfigService configSvc,
        ILogger<JiraService> logger)
    {
        _httpFactory = httpFactory;
        _configSvc   = configSvc;
        _logger      = logger;
    }

    public bool IsConfigured => _configSvc.IsConfigured;

    private HttpClient CreateClient()
    {
        var cfg    = _configSvc.GetConfig();
        var client = _httpFactory.CreateClient("jira");
        var creds  = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.Email}:{cfg.ApiToken}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<List<JiraIssue>> GetIssuesAsync(
        string? jql = null, int maxResults = 50, CancellationToken ct = default)
    {
        var cfg = _configSvc.GetConfig();
        var effectiveJql = jql ?? cfg.DefaultJql ??
            (cfg.DefaultProjectKey is not null
                ? $"project = {cfg.DefaultProjectKey} AND statusCategory != Done ORDER BY rank ASC"
                : "ORDER BY rank ASC");

        var url = $"{cfg.BaseUrl.TrimEnd('/')}/rest/api/3/search/jql";
        var payload = JsonSerializer.Serialize(new
        {
            jql        = effectiveJql,
            maxResults,
            fields     = new[] { "summary", "description", "status", "issuetype", "priority", "assignee", "labels", "customfield_10016" },
        });

        try
        {
            var resp = await CreateClient().PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jira search returned {Status}", resp.StatusCode);
                return [];
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonSerializer.Deserialize<JsonElement>(body, _jsonOpts);
            return ParseIssues(root, cfg.BaseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetIssuesAsync failed");
            return [];
        }
    }

    public async Task<JiraIssue?> GetIssueAsync(string issueKey, CancellationToken ct = default)
    {
        var cfg = _configSvc.GetConfig();
        var url = $"{cfg.BaseUrl.TrimEnd('/')}/rest/api/3/issue/{issueKey}" +
                  "?fields=summary,description,status,issuetype,priority,assignee,labels,customfield_10016";

        try
        {
            var resp = await CreateClient().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body    = await resp.Content.ReadAsStringAsync(ct);
            var element = JsonSerializer.Deserialize<JsonElement>(body, _jsonOpts);
            return ParseSingleIssue(element, cfg.BaseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetIssueAsync failed for {Key}", issueKey);
            return null;
        }
    }

    public async Task<List<JiraTransition>> GetTransitionsAsync(string issueKey, CancellationToken ct = default)
    {
        var cfg = _configSvc.GetConfig();
        var url = $"{cfg.BaseUrl.TrimEnd('/')}/rest/api/3/issue/{issueKey}/transitions";

        try
        {
            var resp = await CreateClient().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];
            var body    = await resp.Content.ReadAsStringAsync(ct);
            var element = JsonSerializer.Deserialize<JsonElement>(body, _jsonOpts);
            if (!element.TryGetProperty("transitions", out var arr)) return [];

            var result = new List<JiraTransition>();
            foreach (var t in arr.EnumerateArray())
            {
                var toStatus = new JiraStatus();
                if (t.TryGetProperty("to", out var to))
                {
                    toStatus.Id   = to.TryGetProperty("id",   out var tid)   ? tid.GetString()   ?? "" : "";
                    toStatus.Name = to.TryGetProperty("name", out var tname) ? tname.GetString() ?? "" : "";
                    if (to.TryGetProperty("statusCategory", out var sc))
                        toStatus.StatusCategory = sc.TryGetProperty("name", out var scn) ? scn.GetString() ?? "" : "";
                }
                result.Add(new JiraTransition
                {
                    Id   = t.TryGetProperty("id",   out var id)   ? id.GetString()   ?? "" : "",
                    Name = t.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    To   = toStatus,
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTransitionsAsync failed for {Key}", issueKey);
            return [];
        }
    }

    public async Task<bool> TransitionIssueAsync(
        string issueKey, string statusName, CancellationToken ct = default)
    {
        var transitions = await GetTransitionsAsync(issueKey, ct);
        var target = transitions.FirstOrDefault(t =>
            string.Equals(t.Name,    statusName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.To.Name, statusName, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            _logger.LogWarning("No transition to '{StatusName}' found for {Key}", statusName, issueKey);
            return false;
        }

        var cfg     = _configSvc.GetConfig();
        var url     = $"{cfg.BaseUrl.TrimEnd('/')}/rest/api/3/issue/{issueKey}/transitions";
        var payload = JsonSerializer.Serialize(new { transition = new { id = target.Id } });

        try
        {
            var resp = await CreateClient().PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Transition failed for {Key}: {Status}", issueKey, resp.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TransitionIssueAsync failed for {Key}", issueKey);
            return false;
        }
    }

    public Task<bool> TransitionToReviewAsync(string issueKey, CancellationToken ct = default)
        => TransitionIssueAsync(issueKey, _configSvc.GetConfig().ReviewStatusName, ct);

    public string FormatAsPrompt(JiraIssue issue)
    {
        var sb = new StringBuilder();
        sb.Append($"[{issue.Key}] {issue.Summary}");
        var desc = JiraDtosHelpers.ToPlainText(issue.Description);
        if (!string.IsNullOrWhiteSpace(desc))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(desc);
        }
        return sb.ToString();
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    private static List<JiraIssue> ParseIssues(JsonElement root, string baseUrl)
    {
        if (!root.TryGetProperty("issues", out var arr)) return [];
        var result = new List<JiraIssue>();
        foreach (var item in arr.EnumerateArray())
        {
            var issue = ParseSingleIssue(item, baseUrl);
            if (issue is not null) result.Add(issue);
        }
        return result;
    }

    private static JiraIssue? ParseSingleIssue(JsonElement item, string baseUrl)
    {
        if (!item.TryGetProperty("key", out var keyEl)) return null;
        var key = keyEl.GetString() ?? "";

        var hasFields = item.TryGetProperty("fields", out var fields);

        var issue = new JiraIssue
        {
            Key     = key,
            Summary = hasFields && fields.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
            Url     = $"{baseUrl.TrimEnd('/')}/browse/{key}",
        };

        if (!hasFields) return issue;

        if (fields.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null)
            issue.Description = desc.GetRawText();

        if (fields.TryGetProperty("status", out var st) && st.ValueKind != JsonValueKind.Null)
        {
            issue.Status = new JiraStatus
            {
                Id             = st.TryGetProperty("id",   out var sid)  ? sid.GetString()  ?? "" : "",
                Name           = st.TryGetProperty("name", out var sn)   ? sn.GetString()   ?? "" : "",
                StatusCategory = st.TryGetProperty("statusCategory", out var sc)
                                   ? (sc.TryGetProperty("name", out var scn) ? scn.GetString() ?? "" : "")
                                   : "",
            };
        }

        if (fields.TryGetProperty("issuetype", out var it) && it.ValueKind != JsonValueKind.Null)
            issue.Issuetype = it.TryGetProperty("name", out var itn) ? itn.GetString() ?? "" : "";

        if (fields.TryGetProperty("priority", out var pr) && pr.ValueKind != JsonValueKind.Null)
            issue.Priority = pr.TryGetProperty("name", out var prn) ? prn.GetString() ?? "" : "";

        if (fields.TryGetProperty("assignee", out var asgn) && asgn.ValueKind != JsonValueKind.Null)
            issue.Assignee = asgn.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;

        if (fields.TryGetProperty("labels", out var lbls) && lbls.ValueKind == JsonValueKind.Array)
            issue.Labels = lbls.EnumerateArray()
                               .Select(l => l.GetString() ?? "")
                               .Where(l => l.Length > 0)
                               .ToList();

        // Story points: customfield_10016 is the standard Jira Cloud field
        if (fields.TryGetProperty("customfield_10016", out var sp) && sp.ValueKind == JsonValueKind.Number)
            issue.StoryPoints = sp.GetDouble();

        return issue;
    }
}
