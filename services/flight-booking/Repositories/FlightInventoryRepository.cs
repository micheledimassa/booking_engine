using flight_booking.Models;
using Npgsql;

namespace flight_booking.Repositories;

public sealed class FlightInventoryRepository : IFlightInventoryRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public FlightInventoryRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task EnsureDepartureAvailabilityAsync(BookingPayload payload, CancellationToken cancellationToken)
    {
        if (payload.Partenza_Sync_Id is null && string.IsNullOrWhiteSpace(payload.Partenza_Id))
            throw new InvalidOperationException("Partenza non specificata.");

        if (payload.Partenza_Sync_Id is null)
            return; 

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT posti_disponibili, is_open
            FROM search_flight
            WHERE id = @id
            ORDER BY updated_at DESC
            LIMIT 1;
        ", conn);

        cmd.Parameters.AddWithValue("id", payload.Partenza_Sync_Id.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Partenza non sincronizzata. Chiamare v1/sync/flight prima di prenotare.");

        var postiDisponibili = reader.GetInt32(reader.GetOrdinal("posti_disponibili"));
        var isOpen = reader.GetBoolean(reader.GetOrdinal("is_open"));

        if (!isOpen)
            throw new InvalidOperationException("La partenza risulta chiusa a nuove prenotazioni.");

        if (postiDisponibili < payload.Posti)
            throw new InvalidOperationException("Posti insufficienti per completare la prenotazione.");
    }

    public async Task ApplySeatDeltaAsync(Guid? partenzaSyncId, int delta, CancellationToken cancellationToken)
    {
        if (partenzaSyncId is null || delta == 0)
            return;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE search_flight
            SET posti_disponibili = posti_disponibili - @delta,
                is_open = CASE WHEN posti_disponibili - @delta <= 0 THEN FALSE ELSE TRUE END,
                updated_at = now()
            WHERE id = @id
              AND (@delta <= 0 OR posti_disponibili - @delta >= 0)
            RETURNING posti_disponibili;
        ", conn);

        cmd.Parameters.AddWithValue("id", partenzaSyncId.Value);
        cmd.Parameters.AddWithValue("delta", delta);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);

        if (result is null)
            throw new InvalidOperationException("Impossibile aggiornare i posti disponibili (verificare disponibilità).");
    }
}
