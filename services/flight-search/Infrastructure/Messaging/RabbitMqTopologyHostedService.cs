using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace flight_search.Infrastructure.Messaging;

public sealed class RabbitMqTopologyHostedService : IHostedService
{
    private readonly IRabbitMqConnectionFactory _connectionFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqTopologyHostedService> _logger;

    public RabbitMqTopologyHostedService(
        IRabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTopologyHostedService> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = _connectionFactory.GetConnection();
            using var channel = connection.CreateModel();

            channel.ExchangeDeclare(_options.InventoryCommandExchange, ExchangeType.Direct, durable: true);
            channel.ExchangeDeclare(_options.InventoryEventExchange, ExchangeType.Topic, durable: true);
            channel.ExchangeDeclare(_options.BookingEventExchange, ExchangeType.Topic, durable: true);
            channel.ExchangeDeclare(_options.FrappeEventExchange, ExchangeType.Topic, durable: true);

            channel.QueueDeclare("inventory.seat-delta", durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"] = "inventory.dlx"
            });
            channel.QueueBind("inventory.seat-delta", _options.InventoryCommandExchange, "inventory.seat_delta.requested");

            channel.ExchangeDeclare("inventory.dlx", ExchangeType.Fanout, durable: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore durante la dichiarazione della topology RabbitMQ (flight-search)");
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
