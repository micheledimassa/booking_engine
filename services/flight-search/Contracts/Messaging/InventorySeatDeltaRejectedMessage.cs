namespace flight_search.Contracts.Messaging;

public sealed record InventorySeatDeltaRejectedMessage
{
    public required Guid MessageId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid BookingId { get; init; }
    public required Guid PartenzaSyncId { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset RejectedAt { get; init; }
}
