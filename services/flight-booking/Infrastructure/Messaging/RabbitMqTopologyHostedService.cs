using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace flight_booking.Infrastructure.Messaging;
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

            channel.ExchangeDeclare(_options.BookingExchange, ExchangeType.Topic, durable: true);
            channel.ExchangeDeclare(_options.InventoryCommandExchange, ExchangeType.Direct, durable: true);
            channel.ExchangeDeclare(_options.InventoryEventExchange, ExchangeType.Topic, durable: true);
            channel.ExchangeDeclare(_options.FrappeCommandExchange, ExchangeType.Direct, durable: true);
            channel.ExchangeDeclare(_options.FrappeEventExchange, ExchangeType.Topic, durable: true);

            channel.QueueDeclare(
                queue: "booking.saga",
                durable: true,
                exclusive: false,
                autoDelete: false);
            channel.QueueBind("booking.saga", _options.BookingExchange, routingKey: "booking.requested");

            channel.QueueDeclare(
                queue: "booking.inventory-events",
                durable: true,
                exclusive: false,
                autoDelete: false);
            channel.QueueBind("booking.inventory-events", _options.InventoryEventExchange, routingKey: "inventory.seat_delta.*");

            channel.QueueDeclare(
                queue: "booking.frappe-commands",
                durable: true,
                exclusive: false,
                autoDelete: false);
            channel.QueueBind("booking.frappe-commands", _options.FrappeCommandExchange, routingKey: "frappe.booking.upsert.requested");

            channel.QueueDeclare(
                queue: "booking.frappe-commands.retry",
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object>
                {
                    ["x-dead-letter-exchange"] = _options.FrappeCommandExchange,
                    ["x-dead-letter-routing-key"] = "frappe.booking.upsert.requested"
                });

            channel.QueueBind(
                queue: "booking.frappe-commands.retry",
                exchange: _options.FrappeCommandExchange,
                routingKey: "frappe.booking.upsert.retry");

            channel.QueueDeclare(
                queue: "booking.frappe-events",
                durable: true,
                exclusive: false,
                autoDelete: false);
            channel.QueueBind("booking.frappe-events", _options.FrappeEventExchange, routingKey: "frappe.booking.upsert.*");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore durante la dichiarazione della topology RabbitMQ");
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
