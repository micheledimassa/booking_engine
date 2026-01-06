using System.Text.Json;
using System.Text.Json.Nodes;
using flight_booking.Contracts;
using flight_booking.Contracts.Messaging;
using flight_booking.Infrastructure.Messaging;
using flight_booking.Infrastructure.Outbox;
using flight_booking.Models;
using flight_booking.Repositories;
using Microsoft.Extensions.Options;

namespace flight_booking.Services;

/// <summary>
/// Gestisce l'ingresso HTTP della prenotazione e orchestra la persistenza + outbox.
/// La logica di inventory/Frappe è delegata ai worker asincroni.
/// </summary>
public sealed class BookingService
{
    private static readonly JsonSerializerOptions MessageSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null
    };

    private readonly IBookingRepository _bookingRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly RabbitMqOptions _rabbitOptions;
    private readonly ILogger<BookingService> _logger;

    public BookingService(
        IBookingRepository bookingRepository,
        IOutboxRepository outboxRepository,
        IOptions<RabbitMqOptions> rabbitOptions,
        ILogger<BookingService> logger)
    {
        _bookingRepository = bookingRepository;
        _outboxRepository = outboxRepository;
        _rabbitOptions = rabbitOptions.Value;
        _logger = logger;
    }

    public async Task<BookingAcceptedResponse> HandleAsync(BookingWebhookRequest request, CancellationToken cancellationToken)
    {
        var payload = BookingPayload.FromRequest(request);

        if (payload.Partenza_Sync_Id is null)
            throw new InvalidOperationException("Partenza_Sync_Id è obbligatoria per completare la prenotazione.");

        payload = payload with { Stato = BookingState.Received };

        await _bookingRepository.UpsertAsync(payload, cancellationToken);
        await PublishBookingRequestedAsync(payload, cancellationToken);

        _logger.LogInformation("Booking {BookingId} accettata e pubblicata in outbox", payload.Id);

        return new BookingAcceptedResponse
        {
            BookingId = payload.Id,
            Status = "RECEIVED"
        };
    }

    private Task PublishBookingRequestedAsync(BookingPayload payload, CancellationToken cancellationToken)
    {
        var message = new BookingRequestedMessage
        {
            MessageId = Guid.NewGuid(),
            BookingId = payload.Id,
            CorrelationId = payload.Id,
            IdempotencyKey = $"{payload.Id}:booking.requested",
            PartenzaSyncId = payload.Partenza_Sync_Id,
            PartenzaId = payload.Partenza_Id,
            Posti = payload.Posti,
            ImportoTotale = payload.Importo_Totale,
            Canale = payload.Canale,
            Gruppo = payload.Gruppo,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var payloadJson = JsonSerializer.SerializeToNode(message, MessageSerializerOptions) as JsonObject
                          ?? new JsonObject();

        var headers = new JsonObject
        {
            ["x-correlation-id"] = message.CorrelationId.ToString(),
            ["x-retry-count"] = 0
        };

        var draft = new OutboxMessageDraft
        {
            AggregateId = message.BookingId,
            Type = "booking.requested",
            Exchange = _rabbitOptions.BookingExchange,
            RoutingKey = "booking.requested",
            Payload = payloadJson,
            Headers = headers,
            MessageId = message.MessageId.ToString(),
            CorrelationId = message.CorrelationId.ToString(),
            IdempotencyKey = message.IdempotencyKey
        };

        return _outboxRepository.EnqueueAsync(draft, cancellationToken);
    }
}
