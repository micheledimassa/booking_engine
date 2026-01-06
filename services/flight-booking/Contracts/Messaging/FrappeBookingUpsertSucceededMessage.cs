namespace flight_booking.Contracts.Messaging;

public sealed record FrappeBookingUpsertSucceededMessage
{
    public required Guid MessageId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid BookingId { get; init; }
    public required string Name { get; init; }
    public required int DocStatus { get; init; }
    public DateTimeOffset SyncedAt { get; init; }
}
