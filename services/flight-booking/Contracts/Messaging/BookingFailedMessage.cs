namespace flight_booking.Contracts.Messaging;

public sealed record BookingFailedMessage
{
    public required Guid MessageId { get; init; }
    public required Guid BookingId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset FailedAt { get; init; }
}
