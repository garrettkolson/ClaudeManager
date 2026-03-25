using ClaudeManager.Hub.Services;
using Moq;

namespace ClaudeManager.Hub.Tests.Helpers;

public class SessionStoreBuilder
{
    private readonly DashboardNotifier _notifier = new();
    private IPersistenceQueue _queue = Mock.Of<IPersistenceQueue>();

    public SessionStoreBuilder WithQueue(IPersistenceQueue q) { _queue = q; return this; }

    public (SessionStore store, DashboardNotifier notifier) Build()
    {
        var store = new SessionStore(_notifier);
        store.SetPersistenceQueue(_queue);
        return (store, _notifier);
    }
}
