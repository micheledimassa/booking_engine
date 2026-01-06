using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace flight_booking.Infrastructure.Outbox;

public sealed class OutboxRepository : IOutboxRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;

    public OutboxRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetUnpublishedAsync(int batchSize, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT id, aggregate_id, type, exchange, routing_key, payload, headers,
                   message_id, correlation_id, idempotency_key, retry_count,
                   created_at, published_at
            FROM outbox_messages
            WHERE published_at IS NULL
            ORDER BY created_at
            LIMIT @batchSize;";

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("batchSize", batchSize);

        var messages = new List<OutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = JsonNode.Parse(reader.GetString(reader.GetOrdinal("payload"))) as JsonObject ?? new JsonObject();
            var headers = JsonNode.Parse(reader.GetString(reader.GetOrdinal("headers"))) as JsonObject ?? new JsonObject();

            messages.Add(new OutboxMessage
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                AggregateId = reader.GetGuid(reader.GetOrdinal("aggregate_id")),
                Type = reader.GetString(reader.GetOrdinal("type")),
                Exchange = reader.GetString(reader.GetOrdinal("exchange")),
                RoutingKey = reader.GetString(reader.GetOrdinal("routing_key")),
                Payload = payload,
                Headers = headers,
                MessageId = reader.GetString(reader.GetOrdinal("message_id")),
                CorrelationId = reader.GetString(reader.GetOrdinal("correlation_id")),
                IdempotencyKey = reader.GetString(reader.GetOrdinal("idempotency_key")),
                RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                PublishedAt = reader.IsDBNull(reader.GetOrdinal("published_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("published_at"))
            });
        }

        return messages;
    }

    public async Task MarkPublishedAsync(long outboxId, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE outbox_messages SET published_at = now() WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", outboxId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueAsync(OutboxMessageDraft draft, CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO outbox_messages (
                aggregate_id, type, exchange, routing_key,
                payload, headers, message_id, correlation_id, idempotency_key,
                retry_count, created_at
            ) VALUES (
                @aggregate_id, @type, @exchange, @routing_key,
                @payload, @headers, @message_id, @correlation_id, @idempotency_key,
                0, now()
            );";

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var payloadJson = JsonSerializer.Serialize(draft.Payload ?? new JsonObject(), SerializerOptions);
        var headersJson = JsonSerializer.Serialize(draft.Headers ?? new JsonObject(), SerializerOptions);

        cmd.Parameters.AddWithValue("aggregate_id", draft.AggregateId);
        cmd.Parameters.AddWithValue("type", draft.Type);
        cmd.Parameters.AddWithValue("exchange", draft.Exchange);
        cmd.Parameters.AddWithValue("routing_key", draft.RoutingKey);
        cmd.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payloadJson;
        cmd.Parameters.Add("headers", NpgsqlDbType.Jsonb).Value = headersJson;
        cmd.Parameters.AddWithValue("message_id", draft.MessageId);
        cmd.Parameters.AddWithValue("correlation_id", draft.CorrelationId);
        cmd.Parameters.AddWithValue("idempotency_key", draft.IdempotencyKey);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
