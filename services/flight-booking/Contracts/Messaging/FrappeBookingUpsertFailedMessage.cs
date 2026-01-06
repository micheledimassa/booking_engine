namespace flight_booking.Contracts.Messaging;

public sealed record FrappeBookingUpsertFailedMessage
{
    public required Guid MessageId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid BookingId { get; init; }
    public required string Reason { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset FailedAt { get; init; }
}
