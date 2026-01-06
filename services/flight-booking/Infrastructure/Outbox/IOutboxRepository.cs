namespace flight_booking.Infrastructure.Outbox;

public interface IOutboxRepository
{
    Task<IReadOnlyList<OutboxMessage>> GetUnpublishedAsync(int batchSize, CancellationToken cancellationToken);
    Task MarkPublishedAsync(long outboxId, CancellationToken cancellationToken);
    Task EnqueueAsync(OutboxMessageDraft draft, CancellationToken cancellationToken);
}
