using ClaudeManager.Hub.Persistence.Entities;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Event bus for LLM deployment status changes.
/// Blazor components subscribe to <see cref="DeploymentChanged"/> and call
/// <c>InvokeAsync(StateHasChanged)</c> to refresh the UI.
/// </summary>
public class LlmDeploymentNotifier
{
    public event Action<LlmDeploymentEntity>? DeploymentChanged;

    public void NotifyChanged(LlmDeploymentEntity deployment) =>
        DeploymentChanged?.Invoke(deployment);
}
