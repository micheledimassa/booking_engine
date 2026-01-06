namespace flight_search.Contracts.Messaging;

public sealed record InventorySeatDeltaAppliedMessage
{
    public required Guid MessageId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid BookingId { get; init; }
    public required Guid PartenzaSyncId { get; init; }
    public required int DeltaApplied { get; init; }
    public required int PostiResidui { get; init; }
    public DateTimeOffset AppliedAt { get; init; }
}
