using flight_booking.Models;

namespace flight_booking.Contracts.Messaging;

public sealed record FrappeBookingUpsertRequestedMessage
{
    public required Guid MessageId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid BookingId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required BookingPayload Payload { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
