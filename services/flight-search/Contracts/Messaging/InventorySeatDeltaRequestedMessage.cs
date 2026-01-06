namespace flight_search.Contracts.Messaging;

public sealed record InventorySeatDeltaRequestedMessage
{
    public required Guid MessageId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required Guid BookingId { get; init; }
    public required Guid PartenzaSyncId { get; init; }
    public required int Delta { get; init; }
    public string Reason { get; init; } = "booking.requested";
    public DateTimeOffset CreatedAt { get; init; }
}
