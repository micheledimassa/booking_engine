namespace flight_booking.Contracts.Messaging;

public sealed record BookingRequestedMessage
{
    public required Guid MessageId { get; init; }
    public required Guid BookingId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required Guid? PartenzaSyncId { get; init; }
    public string? PartenzaId { get; init; }
    public required int Posti { get; init; }
    public required decimal ImportoTotale { get; init; }
    public required string Canale { get; init; }
    public string? Gruppo { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
