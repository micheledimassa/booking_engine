using System.Text.Json.Nodes;

namespace flight_booking.Infrastructure.Outbox;

public sealed record OutboxMessageDraft
{
    public Guid AggregateId { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Exchange { get; init; } = string.Empty;
    public string RoutingKey { get; init; } = string.Empty;
    public JsonObject Payload { get; init; } = new();
    public JsonObject Headers { get; init; } = new();
    public string MessageId { get; init; } = Guid.NewGuid().ToString();
    public string CorrelationId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
}
