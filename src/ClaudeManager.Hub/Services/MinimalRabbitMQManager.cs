using System.Collections.Generic;
using System.Threading.Tasks;
using FoxHire.RabbitMQ.Interfaces;
using FoxHire.RabbitMQ.Models;
using FoxHire.RabbitMQ.Models.Config;
using FoxHire.RabbitMQ.Models.ViewModels;
using FoxHire.RabbitMQ.Models.EventArgs;
using FoxHire.RabbitMQ.Services.Connections;
using FoxHire.RabbitMQ.Services.Topology;
using FoxHire.RabbitMQ.Workers;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client.Events;

namespace ClaudeManager.Hub.Services;

/// <summary>
/// Minimal IRabbitMQManager for Hub (subscribe-only, no DB tracking).
/// RabbitMQScopedSubscribeWorker calls scope.ServiceProvider.GetService&lt;IRabbitMQManager&gt;()
/// internally for RecordMessageReceipt — need a real impl here.
/// </summary>
public class MinimalRabbitMQManager : IRabbitMQManager
{
    private readonly IRabbitMQConnService _connService;
    private readonly IRabbitMQChannelService _channelService;
    private readonly IRabbitMQExchangeManagementService _exchangeService;
    private readonly IRabbitMQQueueManagementService _queueService;
    private readonly IServiceProvider _serviceProvider;
    private readonly List<Task> _pendingSubscribeTasks = new();

    public MinimalRabbitMQManager(
        IRabbitMQConnService connService,
        IRabbitMQChannelService channelService,
        IRabbitMQExchangeManagementService exchangeService,
        IRabbitMQQueueManagementService queueService,
        IServiceProvider serviceProvider)
    {
        _connService = connService;
        _channelService = channelService;
        _exchangeService = exchangeService;
        _queueService = queueService;
        _serviceProvider = serviceProvider;
    }

    public Task SubscribeAll(IEnumerable<Task> subscribeTasks)
    {
        _pendingSubscribeTasks.AddRange(subscribeTasks);
        return Task.CompletedTask;
    }

    public RabbitMQDashboardViewModel GetDashboardViewModel(bool secure = false)
    {
        return new RabbitMQDashboardViewModel
        {
            MainConnectionString = _connService.GetMainBrokerDashboardUrl(secure),
            FailoverConnectionString = _connService.GetFailoverBrokerDashboardUrl(secure)
        };
    }

    public Task Publish(string serviceName, object obj, IExchange exchange, ChannelConfig config = null)
    {
        return Task.CompletedTask;
    }

    public Task SubscribeHandler<TPayload, THandlerService>(
        string handlerName, EventRoutingConfig routingInfo)
        where TPayload : class
        where THandlerService : IEventSubscriber
    {
        var task = ExecuteSubscribeAsync<TPayload, THandlerService>(handlerName, routingInfo);
        _pendingSubscribeTasks.Add(task);
        return task;
    }

    private async Task ExecuteSubscribeAsync<TPayload, THandlerService>(
        string handlerName, EventRoutingConfig routingInfo)
        where TPayload : class
        where THandlerService : IEventSubscriber
    {
        var config = new ChannelConfig { Prefetch = 1 };
        var worker = new RabbitMQScopedSubscribeWorker<THandlerService>(
            _queueService,
            _exchangeService,
            _channelService,
            _serviceProvider,
            config);

        await worker.SubscribeAsyncHandler<TPayload>(handlerName, routingInfo);
    }

    public Task AcknowledgeMessage(string serviceName, ulong deliveryTag)
    {
        return Task.CompletedTask;
    }

    public Task RejectMessage<TPayload>(
        string serviceName,
        FoxHireDeliverEventArgs<TPayload> args,
        bool undeliverable,
        System.Exception ex = null) where TPayload : class
    {
        return Task.CompletedTask;
    }

    public Task RejectMessage<TPayload>(
        string serviceName,
        BasicDeliverEventArgs args,
        bool undeliverable,
        System.Exception ex = null) where TPayload : class
    {
        return Task.CompletedTask;
    }

    public Task RecordMessageReceipt(string serviceName, string messageId)
    {
        return Task.CompletedTask;
    }
}
