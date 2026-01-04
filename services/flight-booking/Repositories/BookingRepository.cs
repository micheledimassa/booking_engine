using System.Text.Json;
using flight_booking.Models;
using Npgsql;
using NpgsqlTypes;

namespace flight_booking.Repositories;

public sealed class BookingRepository : IBookingRepository
{
    private const string TableName = "flight_booking";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NpgsqlDataSource _dataSource;

    public BookingRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<BookingRecord?> UpsertAsync(BookingPayload payload, CancellationToken cancellationToken)
    {
        var previous = await GetAsync(payload.Id, cancellationToken);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($@"
            INSERT INTO {TableName} (
                id, partenza_sync_id, partenza_id, doc_status, stato, canale,
                posti, importo_totale, valuta, doc_name, note, gruppo, raw_payload,
                created_at, updated_at
            ) VALUES (
                @id, @partenza_sync_id, @partenza_id, @doc_status, @stato, @canale,
                @posti, @importo_totale, @valuta, @doc_name, @note, @gruppo, @raw_payload,
                now(), now()
            )
            ON CONFLICT (id) DO UPDATE SET
                partenza_sync_id = EXCLUDED.partenza_sync_id,
                partenza_id = EXCLUDED.partenza_id,
                doc_status = EXCLUDED.doc_status,
                stato = EXCLUDED.stato,
                canale = EXCLUDED.canale,
                posti = EXCLUDED.posti,
                importo_totale = EXCLUDED.importo_totale,
                valuta = EXCLUDED.valuta,
                doc_name = COALESCE(EXCLUDED.doc_name, {TableName}.doc_name),
                note = EXCLUDED.note,
                gruppo = EXCLUDED.gruppo,
                raw_payload = EXCLUDED.raw_payload,
                updated_at = now();
        ", conn);

        cmd.Parameters.AddWithValue("id", payload.Id);
        cmd.Parameters.AddWithValue("partenza_sync_id", (object?)payload.Partenza_Sync_Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("partenza_id", (object?)payload.Partenza_Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("doc_status", payload.DocStatus);
        cmd.Parameters.AddWithValue("stato", payload.Stato);
        cmd.Parameters.AddWithValue("canale", payload.Canale);
        cmd.Parameters.AddWithValue("posti", payload.Posti);
        cmd.Parameters.AddWithValue("importo_totale", payload.Importo_Totale);
        cmd.Parameters.AddWithValue("valuta", payload.Valuta);
        cmd.Parameters.AddWithValue("doc_name", (object?)payload.DocName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("note", (object?)payload.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("gruppo", (object?)payload.Gruppo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("raw_payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(payload, SerializerOptions));

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return previous;
    }

    public async Task MarkSyncedAsync(Guid bookingId, BookingSyncResponse response, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($@"
            UPDATE {TableName}
            SET doc_status = @doc_status,
                stato = CASE @doc_status WHEN 1 THEN 'Confermata' WHEN 2 THEN 'Cancellata' ELSE 'Bozza' END,
                doc_name = COALESCE(@doc_name, doc_name),
                updated_at = now(),
                last_synced_at = now()
            WHERE id = @id;
        ", conn);

        cmd.Parameters.AddWithValue("id", bookingId);
        cmd.Parameters.AddWithValue("doc_status", response.DocStatus);
        cmd.Parameters.AddWithValue("doc_name", (object?)response.Name ?? DBNull.Value);

        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
            throw new InvalidOperationException("Prenotazione non trovata durante l'aggiornamento.");
    }

    public async Task<BookingRecord?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($@"
            SELECT id, partenza_sync_id, partenza_id, doc_status, stato, canale,
                   posti, importo_totale, valuta, doc_name, note, gruppo,
                   created_at, updated_at, last_synced_at
            FROM {TableName}
            WHERE id = @id;
        ", conn);

        cmd.Parameters.AddWithValue("id", bookingId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new BookingRecord
        {
            Id = reader.GetGuid(reader.GetOrdinal("id")),
            PartenzaSyncId = reader.IsDBNull(reader.GetOrdinal("partenza_sync_id")) ? null : reader.GetGuid(reader.GetOrdinal("partenza_sync_id")),
            PartenzaId = reader.IsDBNull(reader.GetOrdinal("partenza_id")) ? null : reader.GetString(reader.GetOrdinal("partenza_id")),
            DocStatus = reader.GetInt32(reader.GetOrdinal("doc_status")),
            Stato = reader.GetString(reader.GetOrdinal("stato")),
            Canale = reader.GetString(reader.GetOrdinal("canale")),
            Posti = reader.GetInt32(reader.GetOrdinal("posti")),
            ImportoTotale = reader.GetDecimal(reader.GetOrdinal("importo_totale")),
            Valuta = reader.GetString(reader.GetOrdinal("valuta")),
            DocName = reader.IsDBNull(reader.GetOrdinal("doc_name")) ? null : reader.GetString(reader.GetOrdinal("doc_name")),
            Note = reader.IsDBNull(reader.GetOrdinal("note")) ? null : reader.GetString(reader.GetOrdinal("note")),
            Gruppo = reader.IsDBNull(reader.GetOrdinal("gruppo")) ? null : reader.GetString(reader.GetOrdinal("gruppo")),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
            LastSyncedAt = reader.IsDBNull(reader.GetOrdinal("last_synced_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_synced_at"))
        };
    }
}
