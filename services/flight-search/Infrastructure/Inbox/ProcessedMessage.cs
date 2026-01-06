namespace flight_search.Infrastructure.Inbox;

public sealed record ProcessedMessage
{
    public string MessageId { get; init; } = string.Empty;
    public string Consumer { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Metadata { get; init; }
}
