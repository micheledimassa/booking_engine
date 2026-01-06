using System.Text.Json;
using flight_booking.Infrastructure.Inbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace flight_booking.Infrastructure.Messaging;

public abstract class IdempotentConsumerHostedService : BackgroundService
{
    private readonly IRabbitMqConnectionFactory _connectionFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly string _queueName;
    private IModel? _channel;

    protected IdempotentConsumerHostedService(
        IRabbitMqConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        string queueName)
    {
        _connectionFactory = connectionFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _queueName = queueName;
    }

    protected virtual ushort PrefetchCount => 10;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = _connectionFactory.GetConnection().CreateModel();
        _channel.BasicQos(0, PrefetchCount, false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) => await HandleDeliveryAsync(ea, stoppingToken);

        _channel.BasicConsume(_queueName, autoAck: false, consumer: consumer);
        _logger.LogInformation("Consumer {Consumer} in ascolto sulla coda {Queue}", GetType().Name, _queueName);
        return Task.CompletedTask;
    }

    private async Task HandleDeliveryAsync(BasicDeliverEventArgs args, CancellationToken ct)
    {
        if (_channel is null)
            return;

        var messageId = args.BasicProperties.MessageId ?? Guid.NewGuid().ToString();

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IProcessedMessageStore>();

            if (await store.ExistsAsync(messageId, ct))
            {
                _channel.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            using var payload = JsonDocument.Parse(args.Body.ToArray());
            await HandleMessageAsync(payload.RootElement, args.BasicProperties, args.RoutingKey, scope.ServiceProvider, ct);

            await store.StoreAsync(new ProcessedMessage
            {
                MessageId = messageId,
                Consumer = GetType().Name,
                ReceivedAt = DateTimeOffset.UtcNow
            }, ct);

            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore elaborando messaggio {MessageId} sulla coda {Queue}", messageId, _queueName);
            _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        base.Dispose();
    }

    protected abstract Task HandleMessageAsync(JsonElement payload, IBasicProperties properties, string routingKey, IServiceProvider services, CancellationToken ct);
}
