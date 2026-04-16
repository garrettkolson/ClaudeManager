using System.Collections.Concurrent;

namespace ClaudeManager.Hub.Services.Jira;

/// <summary>
/// Thread-safe singleton caching on-deck Jira issues.
/// Uses ConcurrentDictionary pattern for atomic operations.
/// </summary>
public class JiraIssueCache
{
    private readonly ConcurrentDictionary<string, JiraIssue> _issues = new();
    private static readonly JiraIssueCache _instance = new();

    private JiraIssueCache() { }

    /// <summary>
    /// Gets the singleton instance of the cache.
    /// </summary>
    public static JiraIssueCache Instance => _instance;

    /// <summary>
    /// Check if issue key exists in cache.
    /// </summary>
    public bool Contains(string issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return false;

        return _issues.ContainsKey(issueKey);
    }

    /// <summary>
    /// Get issue clone (thread-safe copy).
    /// Modifications to the returned instance don't affect cached data.
    /// </summary>
    public JiraIssue? Get(string issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return null;

        if (!_issues.TryGetValue(issueKey, out var issue))
            return null;

        return issue.Clone();
    }

    /// <summary>
    /// Add issue if not exists. Returns true only if issue was newly added.
    /// Returns false if issue already exists or if issue parameter is null.
    /// </summary>
    public bool TryAdd(JiraIssue issue)
    {
        if (issue is null || string.IsNullOrWhiteSpace(issue.Key))
            return false;

        // TryAdd returns the existing value if key already exists,
        // or the added value if key didn't exist
        var addedIssue = _issues.GetOrAdd(issue.Key, issue);

        // Return true only if this was the value added (not an existing one)
        return ReferenceEquals(addedIssue, issue);
    }

    /// <summary>
    /// Remove issue by key. Returns true if issue was removed.
    /// </summary>
    public bool Remove(string issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return false;

        return _issues.TryRemove(issueKey, out _);
    }

    /// <summary>
    /// Get all issue keys in cache.
    /// </summary>
    public IEnumerable<string> GetIssueKeys()
    {
        return _issues.Keys;
    }

    /// <summary>
    /// Get all issues (thread-safe snapshot).
    /// Returns a list of cloned issues.
    /// </summary>
    public List<JiraIssue> GetAllIssues()
    {
        // Create a snapshot by cloning each issue
        var result = new List<JiraIssue>();
        foreach (var kvp in _issues)
        {
            result.Add(kvp.Value.Clone());
        }
        return result;
    }

    /// <summary>
    /// Count of issues in cache.
    /// </summary>
    public int Count => _issues.Count;

    /// <summary>
    /// Clear all issues from cache.
    /// </summary>
    public void Clear()
    {
        _issues.Clear();
    }
}
