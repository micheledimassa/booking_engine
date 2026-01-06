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

public sealed class FrappeEventsWorker : IdempotentConsumerHostedService
{
    private static readonly JsonSerializerOptions MessageSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null
    };

    private readonly RabbitMqOptions _options;
    private readonly ILogger<FrappeEventsWorker> _logger;

    public FrappeEventsWorker(
        IRabbitMqConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<FrappeEventsWorker> logger)
        : base(connectionFactory, scopeFactory, logger, queueName: "booking.frappe-events")
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task HandleMessageAsync(JsonElement payload, IBasicProperties properties, string routingKey, IServiceProvider services, CancellationToken ct)
    {
        var eventType = properties.Type ?? routingKey ?? string.Empty;
        var bookingRepository = services.GetRequiredService<IBookingRepository>();
        var outbox = services.GetRequiredService<IOutboxRepository>();

        switch (eventType)
        {
            case "frappe.booking.upsert.succeeded":
                var success = payload.Deserialize<FrappeBookingUpsertSucceededMessage>()
                              ?? throw new InvalidOperationException("Payload frappe success non valido.");
                await HandleSuccessAsync(success, bookingRepository, outbox, ct);
                break;
            case "frappe.booking.upsert.failed":
                var failure = payload.Deserialize<FrappeBookingUpsertFailedMessage>()
                              ?? throw new InvalidOperationException("Payload frappe failure non valido.");
                await HandleFailureAsync(failure, bookingRepository, outbox, ct);
                break;
            default:
                throw new InvalidOperationException($"Evento Frappe non supportato: {eventType}");
        }
    }

    private async Task HandleSuccessAsync(FrappeBookingUpsertSucceededMessage message, IBookingRepository bookingRepository, IOutboxRepository outbox, CancellationToken ct)
    {
        await bookingRepository.UpdateStateAsync(message.BookingId, BookingState.Confirmed, ct);

        var response = new BookingSyncResponse
        {
            Name = message.Name,
            DocStatus = message.DocStatus,
            Status = "success",
            Uuid = Guid.Empty
        };

        await bookingRepository.MarkSyncedAsync(message.BookingId, response, ct);

        var evt = new BookingConfirmedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = message.BookingId,
            CorrelationId = message.CorrelationId,
            DocName = message.Name,
            ConfirmedAt = message.SyncedAt
        };

        var payloadJson = new JsonObject
        {
            ["messageId"] = evt.MessageId,
            ["bookingId"] = evt.BookingId,
            ["correlationId"] = evt.CorrelationId,
            ["docName"] = evt.DocName,
            ["confirmedAt"] = evt.ConfirmedAt
        };

        var headers = new JsonObject
        {
            ["x-correlation-id"] = evt.CorrelationId.ToString(),
            ["x-retry-count"] = 0
        };

        await outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = evt.BookingId,
            Type = "booking.confirmed",
            Exchange = _options.BookingExchange,
            RoutingKey = "booking.confirmed",
            Payload = payloadJson,
            Headers = headers,
            MessageId = evt.MessageId.ToString(),
            CorrelationId = evt.CorrelationId.ToString(),
            IdempotencyKey = $"{evt.BookingId}:booking.confirmed"
        }, ct);
    }

    private async Task HandleFailureAsync(FrappeBookingUpsertFailedMessage message, IBookingRepository bookingRepository, IOutboxRepository outbox, CancellationToken ct)
    {
        await bookingRepository.UpdateStateAsync(message.BookingId, BookingState.Failed, ct);
        await ReleaseInventoryAsync(message, bookingRepository, outbox, ct);

        var evt = new BookingFailedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = message.BookingId,
            CorrelationId = message.CorrelationId,
            Reason = message.Reason,
            FailedAt = message.FailedAt
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

    private async Task ReleaseInventoryAsync(FrappeBookingUpsertFailedMessage message, IBookingRepository bookingRepository, IOutboxRepository outbox, CancellationToken ct)
    {
        var payload = await bookingRepository.GetPayloadAsync(message.BookingId, ct);
        if (payload?.Partenza_Sync_Id is not Guid partenzaSyncId)
        {
            _logger.LogWarning("Impossibile rilasciare posti per booking {BookingId}: PartenzaSyncId assente", message.BookingId);
            return;
        }

        if (payload.Posti <= 0)
        {
            _logger.LogWarning("Impossibile rilasciare posti per booking {BookingId}: valore posti non valido ({Posti})", message.BookingId, payload.Posti);
            return;
        }

        var releaseCommand = new InventorySeatDeltaRequestedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = message.BookingId,
            CorrelationId = message.CorrelationId,
            IdempotencyKey = $"{message.BookingId}:inventory.release",
            PartenzaSyncId = partenzaSyncId,
            Delta = -Math.Abs(payload.Posti),
            Reason = "booking.failed.frappe",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var payloadJson = JsonSerializer.SerializeToNode(releaseCommand, MessageSerializerOptions) as JsonObject
                          ?? new JsonObject();

        var headers = new JsonObject
        {
            ["x-correlation-id"] = releaseCommand.CorrelationId.ToString(),
            ["x-retry-count"] = 0
        };

        await outbox.EnqueueAsync(new OutboxMessageDraft
        {
            AggregateId = releaseCommand.BookingId,
            Type = "inventory.seat_delta.requested",
            Exchange = _options.InventoryCommandExchange,
            RoutingKey = "inventory.seat_delta.requested",
            Payload = payloadJson,
            Headers = headers,
            MessageId = releaseCommand.MessageId.ToString(),
            CorrelationId = releaseCommand.CorrelationId.ToString(),
            IdempotencyKey = releaseCommand.IdempotencyKey
        }, ct);

        _logger.LogInformation("Rilasciati {Posti} posti per booking {BookingId} dopo failure Frappe", payload.Posti, message.BookingId);
    }
}
