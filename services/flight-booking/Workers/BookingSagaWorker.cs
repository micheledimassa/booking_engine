using System.Text.Json;
using System.Text.Json.Nodes;
using flight_booking.Contracts.Messaging;
using flight_booking.Infrastructure.Messaging;
using flight_booking.Infrastructure.Outbox;
using flight_booking.Models;
using flight_booking.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace flight_booking.Workers;

public sealed class BookingSagaWorker : IdempotentConsumerHostedService
{
    private static readonly JsonSerializerOptions MessageSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null
    };

    private readonly RabbitMqOptions _options;

    public BookingSagaWorker(
        IRabbitMqConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<BookingSagaWorker> logger)
        : base(connectionFactory, scopeFactory, logger, queueName: "booking.saga")
    {
        _options = options.Value;
    }

    protected override async Task HandleMessageAsync(JsonElement payload, IBasicProperties properties, string routingKey, IServiceProvider services, CancellationToken ct)
    {
        var message = payload.Deserialize<BookingRequestedMessage>(MessageSerializerOptions);
        if (message is null)
            throw new InvalidOperationException("Payload booking.requested non valido.");

        if (message.PartenzaSyncId is null)
            throw new InvalidOperationException("PartenzaSyncId mancante nel messaggio booking.requested.");

        var bookingRepository = services.GetRequiredService<IBookingRepository>();
        var outboxRepository = services.GetRequiredService<IOutboxRepository>();

        await bookingRepository.UpdateStateAsync(message.BookingId, BookingState.InventoryPending, ct);

        var inventoryCommand = new InventorySeatDeltaRequestedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = message.BookingId,
            CorrelationId = message.CorrelationId,
            IdempotencyKey = $"{message.BookingId}:inventory.request",
            PartenzaSyncId = message.PartenzaSyncId.Value,
            Delta = message.Posti,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var payloadJson = JsonSerializer.SerializeToNode(inventoryCommand, MessageSerializerOptions) as JsonObject
                          ?? new JsonObject();

        var headers = new JsonObject
        {
            ["x-correlation-id"] = inventoryCommand.CorrelationId.ToString(),
            ["x-retry-count"] = 0
        };

        await outboxRepository.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = inventoryCommand.BookingId,
            Type = "inventory.seat_delta.requested",
            Exchange = _options.InventoryCommandExchange,
            RoutingKey = "inventory.seat_delta.requested",
            Payload = payloadJson,
            Headers = headers,
            MessageId = inventoryCommand.MessageId.ToString(),
            CorrelationId = inventoryCommand.CorrelationId.ToString(),
            IdempotencyKey = inventoryCommand.IdempotencyKey
        }, ct);
    }
}
