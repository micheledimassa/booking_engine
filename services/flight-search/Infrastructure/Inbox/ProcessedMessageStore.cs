using Npgsql;

namespace flight_search.Infrastructure.Inbox;

public sealed class ProcessedMessageStore : IProcessedMessageStore
{
    private readonly NpgsqlDataSource _dataSource;

    public ProcessedMessageStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT 1 FROM processed_messages WHERE message_id = @message_id";
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("message_id", messageId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    public async Task StoreAsync(ProcessedMessage message, CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO processed_messages (message_id, consumer, received_at, metadata)
            VALUES (@message_id, @consumer, @received_at, @metadata)
            ON CONFLICT (message_id) DO NOTHING;";

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("message_id", message.MessageId);
        cmd.Parameters.AddWithValue("consumer", message.Consumer);
        cmd.Parameters.AddWithValue("received_at", message.ReceivedAt);
        cmd.Parameters.AddWithValue("metadata", (object?)message.Metadata ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
