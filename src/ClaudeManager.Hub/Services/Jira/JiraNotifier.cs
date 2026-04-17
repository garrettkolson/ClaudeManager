using ClaudeManager.Hub.Services.Jira;

namespace ClaudeManager.Hub.Services.Jira;

/// <summary>Notifies of On Deck issue events.</summary>
public class JiraNotifier
{
    // Fired when a new On Deck issue is discovered by polling
    public event Action<JiraIssue>? OnDeckIssueAdded;

    // Fired when an issue is removed from On Deck (e.g., status changed)
    public event Action<string>? IssueRemoved;  // issueKey

    public void NotifyDeckIssueAdded(JiraIssue issue) => OnDeckIssueAdded?.Invoke(issue);

    public void NotifyIssueRemoved(string issueKey) => IssueRemoved?.Invoke(issueKey);
}
