using System.Text.Json;
using ClaudeManager.Hub.Persistence;
using ClaudeManager.Hub.Persistence.Entities;
using FoxHire.RabbitMQ.Interfaces;
using FoxHire.RabbitMQ.Models;
using FoxHire.RabbitMQ.Models.Config;
using FoxHire.RabbitMQ.Models.EventArgs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClaudeManager.Hub.Services;

public class ErrorTriggerSubscriber : IEventSubscriber
{
    private readonly IRabbitMQManager _rabbitMqManager;
    private readonly SweAfService _sweAfService;
    private readonly KnownErrorFingerprintService _fingerprintSvc;
    private readonly ErrorTriggerConfig _config;
    private readonly ILogger<ErrorTriggerSubscriber> _logger;
    private readonly IDbContextFactory<ClaudeManagerDbContext> _dbFactory;
    private readonly BuildNotifier _notifier;
    private readonly string _serviceName = nameof(ErrorTriggerSubscriber);
    private readonly EventHandlerRegistry _handlers = new();

    public EventHandlerRegistry Handlers => _handlers;

    public ErrorTriggerSubscriber(
        IRabbitMQManager rabbitMqManager,
        SweAfService sweAfService,
        KnownErrorFingerprintService fingerprintSvc,
        ErrorTriggerConfig config,
        ILogger<ErrorTriggerSubscriber> logger,
        IDbContextFactory<ClaudeManagerDbContext> dbFactory,
        BuildNotifier notifier)
    {
        _rabbitMqManager = rabbitMqManager;
        _sweAfService = sweAfService;
        _fingerprintSvc = fingerprintSvc;
        _config = config;
        _logger = logger;
        _dbFactory = dbFactory;
        _notifier = notifier;
    }

    public async Task SubscribeToEvents()
    {
        _handlers.AddHandler<ErrorTriggerMessage>("errorTriggerHandler", HandleMessage);

        await _rabbitMqManager.SubscribeHandler<ErrorTriggerMessage, ErrorTriggerSubscriber>(
            "errorTriggerHandler",
            new EventRoutingConfig(
                new ExchangeWrapper(_config.ExchangeName, _config.RoutingKey),
                _config.QueueName));

        _logger.LogInformation(
            "ErrorTrigger subscriber started: exchange='{Exchange}', queue='{Queue}'",
            _config.ExchangeName, _config.QueueName);
    }

    private async Task HandleMessage(object? sender, FoxHireDeliverEventArgs<ErrorTriggerMessage> args)
    {
        try
        {
            var payload = args.Payload;
            if (payload is null || payload.Exception?.Message is null or "")
            {
                _logger.LogWarning("ErrorTrigger: received message with no ErrorMessage; ack and skip.");
                await _rabbitMqManager.AcknowledgeMessage(_serviceName, args.DeliveryTag);
                return;
            }

            // Step 1: Compute fingerprint from exception message
            var fingerprint = KnownErrorFingerprintService.ComputeFingerprint(payload.Exception.Message);

            // Step 2: Look up known error
            var known = await _fingerprintSvc.FindByFingerprintAsync(fingerprint);

            // Step 3: Check if we should skip this error
            if (known is not null)
            {
                if (known.Status == KnownErrorStatus.Fixed)
                {
                    _logger.LogInformation(
                        "ErrorTrigger: error {Fingerprint} is FIXED; ack and skip.", fingerprint);
                    await _rabbitMqManager.AcknowledgeMessage(_serviceName, args.DeliveryTag);
                    return;
                }

                if (known.Status == KnownErrorStatus.Deferred
                    && known.NextTriggerAfter.HasValue
                    && known.NextTriggerAfter.Value > DateTimeOffset.UtcNow)
                {
                    _logger.LogInformation(
                        "ErrorTrigger: error {Fingerprint} is DEFERRED until {Next}; ack and skip.",
                        fingerprint, known.NextTriggerAfter);
                    await _rabbitMqManager.AcknowledgeMessage(_serviceName, args.DeliveryTag);
                    return;
                }

                // Known but pending or deferred past trigger date — proceed
                await _fingerprintSvc.IncrementTriggerCountAsync(fingerprint);
            }
            else
            {
                // New unknown error — register as Pending
                var metadataJson = payload.Metadata is JsonElement el ? el.GetRawText() : null;
                await _fingerprintSvc.UpsertAsync(
                    fingerprint,
                    payload.Exception.Message,
                    KnownErrorStatus.Pending,
                    metadataJson: metadataJson);
            }

            // Step 4: Create pending build
            if (_config.RequireApproval)
            {
                await CreatePendingBuildAsync(payload, fingerprint);
            }
            else
            {
                await TriggerBuildAsync(payload, fingerprint);
            }

            _logger.LogInformation(
                "ErrorTrigger: processed error {Fingerprint}", fingerprint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ErrorTrigger handler failed for delivery {Tag}", args.DeliveryTag);
            await _rabbitMqManager.AcknowledgeMessage(_serviceName, args.DeliveryTag);
        }
    }

    private async Task CreatePendingBuildAsync(ErrorTriggerMessage payload, string fingerprint)
    {
        var goal = _config.BuildGoalTemplate
            .Replace("{ErrorDescription}", payload.Exception?.Message ?? "Unknown error")
            .Replace("{ErrorMetadata}", payload.Metadata?.ToString() ?? "");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var now = DateTimeOffset.UtcNow;
        var job = new SweAfJobEntity
        {
            ExternalJobId = "",
            Goal = goal,
            RepoUrl = "",
            Status = BuildStatus.Waiting,
            TriggeredBy = "error",
            ErrorMessage = payload.Exception?.Message,
            Logs = payload.Exception?.StackTraceString,
            CreatedAt = now,
            IsErrorTriggered = true,
        };

        db.SweAfJobs.Add(job);
        await db.SaveChangesAsync();

        await _fingerprintSvc.IncrementTriggerCountAsync(fingerprint);

        _notifier.NotifyBuildChanged(job);

        _logger.LogInformation(
            "ErrorTrigger: created pending build (job={JobId}) for error {Fingerprint}",
            job.Id, fingerprint);
    }

    private async Task TriggerBuildAsync(ErrorTriggerMessage payload, string fingerprint)
    {
        var goal = _config.BuildGoalTemplate
            .Replace("{ErrorDescription}", payload.Exception?.Message ?? "Unknown error")
            .Replace("{ErrorMetadata}", payload.Metadata?.ToString() ?? "");

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<string>(msg =>
                    _logger.LogInformation("ErrorTrigger auto-approve: {Msg}", msg));
                await _sweAfService.TriggerBuildAsync(goal, "", progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ErrorTrigger auto-approve build failed for error {Fingerprint}", fingerprint);
            }
        });
    }
}
