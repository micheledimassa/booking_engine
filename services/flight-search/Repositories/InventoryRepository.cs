using Npgsql;

namespace flight_search.Repositories;

public sealed class InventoryRepository : IInventoryRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public InventoryRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<SeatDeltaResult> ApplySeatDeltaAsync(Guid partenzaId, int delta, CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE search_flight
            SET posti_disponibili = posti_disponibili - @delta,
                is_open = CASE WHEN posti_disponibili - @delta <= 0 THEN FALSE ELSE TRUE END,
                updated_at = now()
            WHERE id = @id
              AND (@delta <= 0 OR posti_disponibili - @delta >= 0)
            RETURNING posti_disponibili;";

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", partenzaId);
        cmd.Parameters.AddWithValue("delta", delta);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is null)
            return new SeatDeltaResult(false, null, "Posti insufficienti o partenza non trovata.");

        var remaining = (int)result;
        return new SeatDeltaResult(true, remaining, null);
    }
}
