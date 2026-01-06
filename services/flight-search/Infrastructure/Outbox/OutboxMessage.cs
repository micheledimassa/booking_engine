using System.Text.Json.Nodes;

namespace flight_search.Infrastructure.Outbox;

public sealed record OutboxMessage
{
    public long Id { get; init; }
    public Guid AggregateId { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Exchange { get; init; } = string.Empty;
    public string RoutingKey { get; init; } = string.Empty;
    public JsonObject Payload { get; init; } = new();
    public JsonObject Headers { get; init; } = new();
    public string MessageId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public int RetryCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}
