using FoxHire.RabbitMQ.Interfaces;

namespace ClaudeManager.Hub.Services;

public class ExchangeWrapper : IExchange
{
    public string ExchangeKey { get; set; }
    public string RoutingKey { get; set; }
    public string ConfigExchangeType { get; set; }
    public bool PersistMessages { get; set; }
    public string MessageMimeType { get; set; }

    public ExchangeWrapper(string exchangeKey, string routingKey)
    {
        ExchangeKey = exchangeKey;
        RoutingKey = routingKey;
        ConfigExchangeType = "topic";
        PersistMessages = true;
        MessageMimeType = "application/json";
    }
}
