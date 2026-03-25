namespace ClaudeManager.Hub.Services;

public interface IWikiService
{
    Task<string?> BuildContextSummaryAsync();
}
