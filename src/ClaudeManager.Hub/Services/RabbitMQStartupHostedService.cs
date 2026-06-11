using Microsoft.Extensions.Options;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// IHostedService wrapper that calls ErrorTriggerSubscriber.SubscribeToEvents()
/// during application startup.
/// </summary>
public class RabbitMQStartupHostedService : IHostedService
{
    private readonly ErrorTriggerSubscriber _subscriber;
    private readonly IServiceProvider _services;

    public RabbitMQStartupHostedService(
        ErrorTriggerSubscriber subscriber,
        IServiceProvider services)
    {
        _subscriber = subscriber;
        _services = services;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Only subscribe if ErrorTriggerConfig is available (may be missing in design-time)
        try
        {
            var config = _services.GetService<ErrorTriggerConfig>();
            if (config is not null)
                _ = _subscriber.SubscribeToEvents();
        }
        catch
        {
            // Design-time: DI not fully built — skip
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
