namespace ClaudeManager.Hub.Services.Jira;

public class JiraPollingService : BackgroundService
{
    private readonly JiraService _jiraSvc;
    private readonly JiraConfigService _configSvc;
    private readonly JiraIssueCache _cache;
    private readonly JiraNotifier _notifier;
    private readonly ILogger<JiraPollingService> _logger;

    public JiraPollingService(
        JiraService jiraSvc,
        JiraConfigService configSvc,
        JiraIssueCache cache,
        JiraNotifier notifier,
        ILogger<JiraPollingService> logger)
    {
        _jiraSvc   = jiraSvc;
        _configSvc = configSvc;
        _cache     = cache;
        _notifier  = notifier;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Brief delay so JiraConfigService.StartAsync completes first
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        await PollAsync(fullSync: true, ct);

        while (!ct.IsCancellationRequested)
        {
            var intervalSecs = Math.Max(10, _configSvc.GetConfig().PollingIntervalSecs);
            await Task.Delay(TimeSpan.FromSeconds(intervalSecs), ct);
            await PollAsync(fullSync: false, ct);
        }
    }

    private async Task PollAsync(bool fullSync, CancellationToken ct)
    {
        if (!_configSvc.IsConfigured) return;

        try
        {
            var cfg = _configSvc.GetConfig();
            var onDeck = $"\"{cfg.OnDeckStatusName}\"";
            string jql;

            if (fullSync)
            {
                jql = string.IsNullOrWhiteSpace(cfg.DefaultProjectKey)
                    ? $"status = {onDeck} ORDER BY rank ASC"
                    : $"project = {cfg.DefaultProjectKey} AND status = {onDeck} ORDER BY rank ASC";
            }
            else
            {
                var buffer = cfg.PollingIntervalSecs + 30;
                jql = string.IsNullOrWhiteSpace(cfg.DefaultProjectKey)
                    ? $"status = {onDeck} AND updated >= -{buffer}s ORDER BY rank ASC"
                    : $"project = {cfg.DefaultProjectKey} AND status = {onDeck} AND updated >= -{buffer}s ORDER BY rank ASC";
            }

            var issues = await _jiraSvc.GetIssuesAsync(jql, 100, ct);

            foreach (var issue in issues)
            {
                if (_cache.TryAdd(issue))
                    _notifier.NotifyDeckIssueAdded(issue);
            }

            // Full sync: evict stale keys no longer On Deck
            if (fullSync)
            {
                var fetchedKeys = issues.Select(i => i.Key).ToHashSet();
                foreach (var key in _cache.GetIssueKeys().ToList())
                {
                    if (!fetchedKeys.Contains(key))
                    {
                        _cache.Remove(key);
                        _notifier.NotifyIssueRemoved(key);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jira polling failed");
        }
    }
}
