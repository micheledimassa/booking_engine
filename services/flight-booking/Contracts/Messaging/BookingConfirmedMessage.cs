namespace flight_booking.Contracts.Messaging;

public sealed record BookingConfirmedMessage
{
    public required Guid MessageId { get; init; }
    public required Guid BookingId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string DocName { get; init; }
    public DateTimeOffset ConfirmedAt { get; init; }
}
