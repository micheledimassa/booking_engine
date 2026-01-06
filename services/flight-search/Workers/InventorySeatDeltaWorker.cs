using System.Text.Json;
using System.Text.Json.Nodes;
using flight_search.Contracts.Messaging;
using flight_search.Infrastructure.Messaging;
using flight_search.Infrastructure.Outbox;
using flight_search.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace flight_search.Workers;

public sealed class InventorySeatDeltaWorker : IdempotentConsumerHostedService
{
    private static readonly JsonSerializerOptions MessageSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null
    };

    private readonly RabbitMqOptions _options;

    public InventorySeatDeltaWorker(
        IRabbitMqConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> rabbitOptions,
        ILogger<InventorySeatDeltaWorker> logger)
        : base(connectionFactory, scopeFactory, logger, queueName: "inventory.seat-delta")
    {
        _options = rabbitOptions.Value;
    }

    protected override async Task HandleMessageAsync(JsonElement payload, IBasicProperties properties, string routingKey, IServiceProvider services, CancellationToken ct)
    {
        var message = payload.Deserialize<InventorySeatDeltaRequestedMessage>(MessageSerializerOptions);
        if (message is null)
            throw new InvalidOperationException("Messaggio inventory.seat_delta.requested non valido.");

        var repository = services.GetRequiredService<IInventoryRepository>();
        var outbox = services.GetRequiredService<IOutboxRepository>();

        var result = await repository.ApplySeatDeltaAsync(message.PartenzaSyncId, message.Delta, ct);

        if (result.Success && result.PostiResidui is int remaining)
        {
            await PublishAppliedAsync(message, remaining, outbox, ct);
        }
        else
        {
            var reason = result.Reason ?? "Impossibile aggiornare i posti disponibili.";
            await PublishRejectedAsync(message, reason, outbox, ct);
        }
    }

    private Task PublishAppliedAsync(InventorySeatDeltaRequestedMessage cmd, int remainingSeats, IOutboxRepository outbox, CancellationToken ct)
    {
        var evt = new InventorySeatDeltaAppliedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = cmd.BookingId,
            CorrelationId = cmd.CorrelationId,
            PartenzaSyncId = cmd.PartenzaSyncId,
            DeltaApplied = cmd.Delta,
            PostiResidui = remainingSeats,
            AppliedAt = DateTimeOffset.UtcNow
        };

        var payload = JsonSerializer.SerializeToNode(evt, MessageSerializerOptions) as JsonObject
                      ?? new JsonObject();

        var headers = new JsonObject
        {
            ["x-correlation-id"] = evt.CorrelationId.ToString(),
            ["x-retry-count"] = 0
        };

        return outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = evt.BookingId,
            Type = "inventory.seat_delta.applied",
            Exchange = _options.InventoryEventExchange,
            RoutingKey = "inventory.seat_delta.applied",
            Payload = payload,
            Headers = headers,
            MessageId = evt.MessageId.ToString(),
            CorrelationId = evt.CorrelationId.ToString(),
            IdempotencyKey = $"{evt.BookingId}:inventory.applied"
        }, ct);
    }

    private Task PublishRejectedAsync(InventorySeatDeltaRequestedMessage cmd, string reason, IOutboxRepository outbox, CancellationToken ct)
    {
        var evt = new InventorySeatDeltaRejectedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = cmd.BookingId,
            CorrelationId = cmd.CorrelationId,
            PartenzaSyncId = cmd.PartenzaSyncId,
            Reason = reason,
            RejectedAt = DateTimeOffset.UtcNow
        };

        var payload = JsonSerializer.SerializeToNode(evt, MessageSerializerOptions) as JsonObject
                      ?? new JsonObject();

        var headers = new JsonObject
        {
            ["x-correlation-id"] = evt.CorrelationId.ToString(),
            ["x-retry-count"] = 0
        };

        return outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = evt.BookingId,
            Type = "inventory.seat_delta.rejected",
            Exchange = _options.InventoryEventExchange,
            RoutingKey = "inventory.seat_delta.rejected",
            Payload = payload,
            Headers = headers,
            MessageId = evt.MessageId.ToString(),
            CorrelationId = evt.CorrelationId.ToString(),
            IdempotencyKey = $"{evt.BookingId}:inventory.rejected"
        }, ct);
    }
}
