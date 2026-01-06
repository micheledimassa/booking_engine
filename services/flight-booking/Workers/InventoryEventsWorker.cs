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

public sealed class InventoryEventsWorker : IdempotentConsumerHostedService
{
    private static readonly JsonSerializerOptions MessageSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null
    };

    private readonly RabbitMqOptions _options;

    public InventoryEventsWorker(
        IRabbitMqConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<InventoryEventsWorker> logger)
        : base(connectionFactory, scopeFactory, logger, queueName: "booking.inventory-events")
    {
        _options = options.Value;
    }

    protected override async Task HandleMessageAsync(JsonElement payload, IBasicProperties properties, string routingKey, IServiceProvider services, CancellationToken ct)
    {
        var eventType = properties.Type ?? routingKey ?? string.Empty;
        var bookingRepository = services.GetRequiredService<IBookingRepository>();
        var outbox = services.GetRequiredService<IOutboxRepository>();

        switch (eventType)
        {
            case "inventory.seat_delta.applied":
                var applied = payload.Deserialize<InventorySeatDeltaAppliedMessage>(MessageSerializerOptions)
                             ?? throw new InvalidOperationException("Payload inventory.seat_delta.applied non valido.");
                await HandleAppliedAsync(applied, bookingRepository, outbox, ct);
                break;
            case "inventory.seat_delta.rejected":
                var rejected = payload.Deserialize<InventorySeatDeltaRejectedMessage>(MessageSerializerOptions)
                              ?? throw new InvalidOperationException("Payload inventory.seat_delta.rejected non valido.");
                await HandleRejectedAsync(rejected, bookingRepository, outbox, ct);
                break;
            default:
                throw new InvalidOperationException($"Evento inventory non supportato: {eventType}");
        }
    }

    private async Task HandleAppliedAsync(InventorySeatDeltaAppliedMessage message, IBookingRepository bookingRepository, IOutboxRepository outbox, CancellationToken ct)
    {
        await bookingRepository.UpdateStateAsync(message.BookingId, BookingState.InventoryApplied, ct);
        var payload = await bookingRepository.GetPayloadAsync(message.BookingId, ct)
                      ?? throw new InvalidOperationException("Payload prenotazione non trovato per invio a Frappe.");

        await bookingRepository.UpdateStateAsync(message.BookingId, BookingState.FrappePending, ct);

        var command = new FrappeBookingUpsertRequestedMessage
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = message.CorrelationId,
            BookingId = message.BookingId,
            IdempotencyKey = $"{message.BookingId}:frappe.request",
            Payload = payload,
            Attempt = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var messageJson = JsonSerializer.SerializeToNode(command, MessageSerializerOptions) as JsonObject
                          ?? throw new InvalidOperationException("Impossibile serializzare il comando Frappe.");

        var headers = new JsonObject
        {
            ["x-correlation-id"] = command.CorrelationId.ToString(),
            ["x-retry-count"] = command.Attempt
        };

        await outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = command.BookingId,
            Type = "frappe.booking.upsert.requested",
            Exchange = _options.FrappeCommandExchange,
            RoutingKey = "frappe.booking.upsert.requested",
            Payload = messageJson,
            Headers = headers,
            MessageId = command.MessageId.ToString(),
            CorrelationId = command.CorrelationId.ToString(),
            IdempotencyKey = command.IdempotencyKey
        }, ct);
    }

    private async Task HandleRejectedAsync(InventorySeatDeltaRejectedMessage message, IBookingRepository bookingRepository, IOutboxRepository outbox, CancellationToken ct)
    {
        await bookingRepository.UpdateStateAsync(message.BookingId, BookingState.Failed, ct);

        var evt = new BookingFailedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = message.BookingId,
            CorrelationId = message.CorrelationId,
            Reason = message.Reason,
            FailedAt = DateTimeOffset.UtcNow
        };

        var payloadJson = new JsonObject
        {
            ["messageId"] = evt.MessageId,
            ["bookingId"] = evt.BookingId,
            ["correlationId"] = evt.CorrelationId,
            ["reason"] = evt.Reason,
            ["failedAt"] = evt.FailedAt
        };

        var headers = new JsonObject
        {
            ["x-correlation-id"] = evt.CorrelationId.ToString(),
            ["x-retry-count"] = 0
        };

        await outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = evt.BookingId,
            Type = "booking.failed",
            Exchange = _options.BookingExchange,
            RoutingKey = "booking.failed",
            Payload = payloadJson,
            Headers = headers,
            MessageId = evt.MessageId.ToString(),
            CorrelationId = evt.CorrelationId.ToString(),
            IdempotencyKey = $"{evt.BookingId}:booking.failed"
        }, ct);
    }
}
