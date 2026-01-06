namespace flight_search.Infrastructure.Inbox;

public interface IProcessedMessageStore
{
    Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken);
    Task StoreAsync(ProcessedMessage message, CancellationToken cancellationToken);
}
