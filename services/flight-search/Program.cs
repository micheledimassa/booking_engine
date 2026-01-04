using System.Linq;
using Npgsql;

// --- CONFIGURAZIONE E AVVIO ---
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var host = Environment.GetEnvironmentVariable("DB_HOST");
var port = Environment.GetEnvironmentVariable("DB_PORT");
var db   = Environment.GetEnvironmentVariable("DB_NAME");
var user = Environment.GetEnvironmentVariable("DB_USER");
var pass = Environment.GetEnvironmentVariable("DB_PASSWORD");

if (new[] { host, port, db, user, pass }.Any(string.IsNullOrWhiteSpace))
    throw new InvalidOperationException("Variabili d'ambiente DB_* mancanti.");

var connString = $"Host={host};Port={port};Database={db};Username={user};Password={pass}";
var apiKey = Environment.GetEnvironmentVariable("API_KEY")
    ?? throw new InvalidOperationException("Variabile d'ambiente API_KEY mancante.");

//c'è async cosi non blocco il thread mentre il db risponde 

// === HEALTH ===
app.MapGet("v1/health", async () =>
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    return Results.Ok(new { status = "ok", db = "connected" });
});

// === ANDATA/RITORNO ===
app.MapGet("v1/flights/andataritorno", async (string from_iata, string to_iata) =>
{
    if (string.IsNullOrWhiteSpace(from_iata) || string.IsNullOrWhiteSpace(to_iata))
        return Results.BadRequest("from_iata e to_iata sono obbligatori.");
    
    //logiche di normalizzazione dei codici iata
    var from = from_iata.Trim().ToUpperInvariant();
    var to   = to_iata.Trim().ToUpperInvariant();

    if (from.Length != 3 || to.Length != 3)
        return Results.BadRequest("I codici IATA devono essere di 3 caratteri.");

    //await per non bloccare il thread
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    var cmd = new NpgsqlCommand(@"
        WITH ranked AS (
            SELECT
                id, gruppo, direzione, volo_id, compagnia,
                data, ora, posti_totali, posti_disponibili,
                prezzo, is_open,
                ROW_NUMBER() OVER (
                    PARTITION BY volo_id, direzione, data, COALESCE(ora, TIME '00:00:00'), from_iata, to_iata
                    ORDER BY updated_at DESC, id DESC
                ) AS rn
            FROM search_flight
            WHERE (from_iata = @from AND to_iata = @to)
               OR (from_iata = @to   AND to_iata = @from)
        )
        SELECT id, gruppo, direzione, volo_id, compagnia,
               data, ora, posti_totali, posti_disponibili,
               prezzo, is_open
        FROM ranked
        WHERE rn = 1
        ORDER BY gruppo, direzione, data, ora NULLS LAST;
    ", conn);

    cmd.Parameters.AddWithValue("from", from);
    cmd.Parameters.AddWithValue("to", to);
    //dizionario per raggruppare i voli
    var gruppi = new Dictionary<string, FlightGroup>();

    await using var reader = await cmd.ExecuteReaderAsync();
    var idx = new
    {
        Id = reader.GetOrdinal("id"),
        Gruppo = reader.GetOrdinal("gruppo"),
        Direzione = reader.GetOrdinal("direzione"),
        VoloId = reader.GetOrdinal("volo_id"),
        Compagnia = reader.GetOrdinal("compagnia"),
        Data = reader.GetOrdinal("data"),
        Ora = reader.GetOrdinal("ora"),
        PostiTot = reader.GetOrdinal("posti_totali"),
        PostiDisp = reader.GetOrdinal("posti_disponibili"),
        Prezzo = reader.GetOrdinal("prezzo"),
        IsOpen = reader.GetOrdinal("is_open")
    };

    //leggo i risultati e li raggruppo

    while (await reader.ReadAsync())
    {
        var gruppo = reader.GetString(idx.Gruppo);
        if (!gruppi.TryGetValue(gruppo, out var bucket))
        {
            bucket = new FlightGroup();
            gruppi[gruppo] = bucket;
        }

        var prezzo = reader.IsDBNull(idx.Prezzo) ? 0m : reader.GetDecimal(idx.Prezzo);
        var seg = new FlightSegment(
            reader.GetGuid(idx.Id).ToString(),
            reader.GetString(idx.Direzione),
            reader.GetString(idx.VoloId),
            reader.IsDBNull(idx.Compagnia) ? null : reader.GetString(idx.Compagnia),
            reader.GetDateTime(idx.Data).ToString("yyyy-MM-dd"),
            reader.IsDBNull(idx.Ora) ? null : reader.GetTimeSpan(idx.Ora).ToString(@"hh\:mm"),
            reader.GetInt32(idx.PostiTot),
            reader.GetInt32(idx.PostiDisp),
            prezzo,
            reader.GetBoolean(idx.IsOpen)
        );

        bucket.Add(seg);
    }

    var voli = gruppi
        .Select(g =>
        {
            var a = g.Value.Andata.OrderBy(x => x.Prezzo).FirstOrDefault();
            var r = g.Value.Ritorno.OrderBy(x => x.Prezzo).FirstOrDefault();
            if (a == null || r == null) return null;

            return new
            {
                gruppo = g.Key,
                andata = new
                {
                    id = a.Id,
                    direzione = "Andata",
                    data = a.Data,
                    ora = a.Ora ?? "00:00:00",
                    partenza_da = from,
                    arrivo_a = to,
                    posti_totali = a.PostiTotali,
                    posti_disponibili = a.PostiDisponibili,
                    volo = a.VoloId,
                    compagnia = a.Compagnia
                },
                ritorno = new
                {
                    id = r.Id,
                    direzione = "Ritorno",
                    data = r.Data,
                    ora = r.Ora ?? "00:00:00",
                    partenza_da = to,
                    arrivo_a = from,
                    posti_totali = r.PostiTotali,
                    posti_disponibili = r.PostiDisponibili,
                    volo = r.VoloId,
                    compagnia = r.Compagnia
                },
                costo_totale = a.Prezzo + r.Prezzo
            };
        })
        .Where(x => x != null)
        .ToList();

    var route = new
    {
        from = new
        {
            iata = from,
        },
        to = new
        {
            iata = to,
        }
    };

    return Results.Ok(new { route, voli });
});

// === ANDATA/RITORNO CON DATE ===
app.MapGet("v1/flights/andataritornodate", async (string from_iata, string to_iata, DateOnly data_andata, DateOnly data_ritorno) =>
{
    if (string.IsNullOrWhiteSpace(from_iata) || string.IsNullOrWhiteSpace(to_iata))
        return Results.BadRequest("from_iata e to_iata sono obbligatori.");

    var from = from_iata.Trim().ToUpperInvariant();
    var to   = to_iata.Trim().ToUpperInvariant();

    if (from.Length != 3 || to.Length != 3)
        return Results.BadRequest("I codici IATA devono essere di 3 caratteri.");

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    var cmd = new NpgsqlCommand(@"
        WITH ranked AS (
            SELECT
                id, gruppo, direzione, volo_id, compagnia,
                data, ora, posti_totali, posti_disponibili,
                prezzo, is_open,
                ROW_NUMBER() OVER (
                    PARTITION BY volo_id, direzione, data, COALESCE(ora, TIME '00:00:00'), from_iata, to_iata
                    ORDER BY updated_at DESC, id DESC
                ) AS rn
            FROM search_flight
            WHERE (
                    from_iata = @from AND to_iata = @to AND data = @data_andata AND direzione = 'A'
                )
               OR (
                    from_iata = @to AND to_iata = @from AND data = @data_ritorno AND direzione = 'R'
                )
        )
        SELECT id, gruppo, direzione, volo_id, compagnia,
               data, ora, posti_totali, posti_disponibili,
               prezzo, is_open
        FROM ranked
        WHERE rn = 1
        ORDER BY gruppo, direzione, data, ora NULLS LAST;
    ", conn);

    cmd.Parameters.AddWithValue("from", from);
    cmd.Parameters.AddWithValue("to", to);
    cmd.Parameters.AddWithValue("data_andata", data_andata.ToDateTime(TimeOnly.MinValue));
    cmd.Parameters.AddWithValue("data_ritorno", data_ritorno.ToDateTime(TimeOnly.MinValue));

    var gruppi = new Dictionary<string, FlightGroup>();

    await using var reader = await cmd.ExecuteReaderAsync();
    var idx = new
    {
        Id = reader.GetOrdinal("id"),
        Gruppo = reader.GetOrdinal("gruppo"),
        Direzione = reader.GetOrdinal("direzione"),
        VoloId = reader.GetOrdinal("volo_id"),
        Compagnia = reader.GetOrdinal("compagnia"),
        Data = reader.GetOrdinal("data"),
        Ora = reader.GetOrdinal("ora"),
        PostiTot = reader.GetOrdinal("posti_totali"),
        PostiDisp = reader.GetOrdinal("posti_disponibili"),
        Prezzo = reader.GetOrdinal("prezzo"),
        IsOpen = reader.GetOrdinal("is_open")
    };

    while (await reader.ReadAsync())
    {
        var gruppo = reader.GetString(idx.Gruppo);
        if (!gruppi.TryGetValue(gruppo, out var bucket))
        {
            bucket = new FlightGroup();
            gruppi[gruppo] = bucket;
        }

        var prezzo = reader.IsDBNull(idx.Prezzo) ? 0m : reader.GetDecimal(idx.Prezzo);
        var seg = new FlightSegment(
            reader.GetGuid(idx.Id).ToString(),
            reader.GetString(idx.Direzione),
            reader.GetString(idx.VoloId),
            reader.IsDBNull(idx.Compagnia) ? null : reader.GetString(idx.Compagnia),
            reader.GetDateTime(idx.Data).ToString("yyyy-MM-dd"),
            reader.IsDBNull(idx.Ora) ? null : reader.GetTimeSpan(idx.Ora).ToString(@"hh\:mm"),
            reader.GetInt32(idx.PostiTot),
            reader.GetInt32(idx.PostiDisp),
            prezzo,
            reader.GetBoolean(idx.IsOpen)
        );

        bucket.Add(seg);
    }

    var voli = gruppi
        .Select(g =>
        {
            var a = g.Value.Andata.OrderBy(x => x.Prezzo).FirstOrDefault();
            var r = g.Value.Ritorno.OrderBy(x => x.Prezzo).FirstOrDefault();
            if (a == null || r == null) return null;

            return new
            {
                gruppo = g.Key,
                andata = new
                {
                    id = a.Id,
                    direzione = "Andata",
                    data = a.Data,
                    ora = a.Ora ?? "00:00:00",
                    partenza_da = from,
                    arrivo_a = to,
                    posti_totali = a.PostiTotali,
                    posti_disponibili = a.PostiDisponibili,
                    volo = a.VoloId,
                    compagnia = a.Compagnia
                },
                ritorno = new
                {
                    id = r.Id,
                    direzione = "Ritorno",
                    data = r.Data,
                    ora = r.Ora ?? "00:00:00",
                    partenza_da = to,
                    arrivo_a = from,
                    posti_totali = r.PostiTotali,
                    posti_disponibili = r.PostiDisponibili,
                    volo = r.VoloId,
                    compagnia = r.Compagnia
                },
                costo_totale = a.Prezzo + r.Prezzo
            };
        })
        .Where(x => x != null)
        .ToList();

    var route = new
    {
        from = new
        {
            iata = from,
    
        },
        to = new
        {
            iata = to,
     
        }
    };

    return Results.Ok(new { route, voli });
});

// === SYNC FLIGHT === 
//sia in primo caricamento che in update dei record 
app.MapPost("v1/sync/flight", async (HttpContext httpContext, FlightSync payload) =>
{
    if (!httpContext.Request.Headers.TryGetValue("X-API-Key", out var providedKey) ||
        !string.Equals(providedKey, apiKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    // normalizza per coerenza con le query di ricerca
    var fromIata = payload.From_Iata.Trim().ToUpperInvariant();
    var toIata = payload.To_Iata.Trim().ToUpperInvariant();
    var direzione = payload.Direzione.Trim().ToUpperInvariant();
    var oraValue = payload.Ora is null ? (object)DBNull.Value : TimeSpan.Parse(payload.Ora) as object;

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var insertCmd = new NpgsqlCommand(@"
        INSERT INTO search_flight (
            id, gruppo, direzione, volo_id, compagnia,
            from_iata, to_iata, data, ora,
            posti_totali, posti_disponibili, prezzo, is_open,
            updated_at
        ) VALUES (
            @id, @gruppo, @direzione, @volo_id, @compagnia,
            @from_iata, @to_iata, @data, @ora,
            @posti_totali, @posti_disponibili, @prezzo, @is_open,
            now()
        )
        ON CONFLICT (id) DO UPDATE SET
            gruppo = EXCLUDED.gruppo,
            direzione = EXCLUDED.direzione,
            volo_id = EXCLUDED.volo_id,
            compagnia = EXCLUDED.compagnia,
            from_iata = EXCLUDED.from_iata,
            to_iata = EXCLUDED.to_iata,
            data = EXCLUDED.data,
            ora = EXCLUDED.ora,
            posti_totali = EXCLUDED.posti_totali,
            posti_disponibili = EXCLUDED.posti_disponibili,
            prezzo = EXCLUDED.prezzo,
            is_open = EXCLUDED.is_open,
            updated_at = now();
    ", conn, tx);

    insertCmd.Parameters.AddWithValue("id", payload.Id);
    insertCmd.Parameters.AddWithValue("gruppo", payload.Gruppo);
    insertCmd.Parameters.AddWithValue("direzione", direzione);
    insertCmd.Parameters.AddWithValue("volo_id", payload.Volo_Id);
    insertCmd.Parameters.AddWithValue("compagnia", payload.Compagnia ?? (object)DBNull.Value);
    insertCmd.Parameters.AddWithValue("from_iata", fromIata);
    insertCmd.Parameters.AddWithValue("to_iata", toIata);
    insertCmd.Parameters.AddWithValue("data", payload.Data.ToDateTime(TimeOnly.MinValue));
    insertCmd.Parameters.AddWithValue("ora", oraValue);
    insertCmd.Parameters.AddWithValue("posti_totali", payload.Posti_Totali);
    insertCmd.Parameters.AddWithValue("posti_disponibili", payload.Posti_Disponibili);
    insertCmd.Parameters.AddWithValue("prezzo", payload.Prezzo);
    insertCmd.Parameters.AddWithValue("is_open", payload.Is_Open);

    await insertCmd.ExecuteNonQueryAsync();
    await tx.CommitAsync();

    return Results.Ok(new { status = "synced" });
});

//cancella tutti i voli di un gruppo
app.MapDelete("v1/sync/group/{gruppo}", async (HttpContext httpContext, string gruppo) =>
{
    if (!httpContext.Request.Headers.TryGetValue("X-API-Key", out var providedKey) ||
        !string.Equals(providedKey, apiKey, StringComparison.Ordinal))
        return Results.Unauthorized();

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    var cmd = new NpgsqlCommand("DELETE FROM search_flight WHERE gruppo = @gruppo;", conn);
    cmd.Parameters.AddWithValue("gruppo", gruppo);
    var affected = await cmd.ExecuteNonQueryAsync();

    return affected > 0 ? Results.Ok(new { status = "deleted", records = affected }) : Results.NotFound();
});

// === INTERNAL INVENTORY API ===
app.MapGet("internal/flights/{id:guid}", async (Guid id, HttpContext httpContext) =>
{
    if (!InternalApiHelper.IsAuthorized(httpContext, apiKey))
        return Results.Unauthorized();

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    var cmd = new NpgsqlCommand(@"
        SELECT posti_disponibili, is_open
        FROM search_flight
        WHERE id = @id
        ORDER BY updated_at DESC
        LIMIT 1;
    ", conn);

    cmd.Parameters.AddWithValue("id", id);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.NotFound(new { error = "Partenza non trovata." });

    var posti = reader.GetInt32(reader.GetOrdinal("posti_disponibili"));
    var isOpen = reader.GetBoolean(reader.GetOrdinal("is_open"));

    return Results.Ok(new { posti_disponibili = posti, is_open = isOpen });
});

app.MapPost("internal/flights/{id:guid}/seat-delta", async (Guid id, HttpContext httpContext, SeatDeltaRequest request) =>
{
    if (!InternalApiHelper.IsAuthorized(httpContext, apiKey))
        return Results.Unauthorized();

    if (request is null)
        return Results.BadRequest(new { error = "Payload mancante." });

    if (request.Delta == 0)
        return Results.Ok(new { status = "noop" });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    var cmd = new NpgsqlCommand(@"
        UPDATE search_flight
        SET posti_disponibili = posti_disponibili - @delta,
            is_open = CASE WHEN posti_disponibili - @delta <= 0 THEN FALSE ELSE TRUE END,
            updated_at = now()
        WHERE id = @id
          AND (@delta <= 0 OR posti_disponibili - @delta >= 0)
        RETURNING posti_disponibili;
    ", conn);

    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("delta", request.Delta);

    var result = await cmd.ExecuteScalarAsync();
    if (result is null)
        return Results.BadRequest(new { error = "Impossibile aggiornare i posti disponibili." });

    return Results.Ok(new { posti_disponibili = (int)result });
});

app.Run("http://0.0.0.0:8080");

// --- tipi sotto --- 
record FlightSegment(
    string Id,
    string Direzione,
    string VoloId,
    string? Compagnia,
    string Data,
    string? Ora,
    int PostiTotali,
    int PostiDisponibili,
    decimal Prezzo,
    bool IsOpen
);

record FlightSync(
    Guid Id,
    string Gruppo,
    string Direzione,
    string Volo_Id,
    string? Compagnia,
    string From_Iata,
    string To_Iata,
    DateOnly Data,
    string? Ora,
    int Posti_Totali,
    int Posti_Disponibili,
    decimal Prezzo,
    bool Is_Open
);

class FlightGroup
{
    public List<FlightSegment> Andata { get; } = new();
    public List<FlightSegment> Ritorno { get; } = new();
    public decimal PrezzoAndata { get; private set; }
    public decimal PrezzoRitorno { get; private set; }
    public void Add(FlightSegment seg)
    {
        if (seg.Direzione == "A") { Andata.Add(seg); PrezzoAndata += seg.Prezzo; }
        else if (seg.Direzione == "R") { Ritorno.Add(seg); PrezzoRitorno += seg.Prezzo; }
    }
}

static class InternalApiHelper
{
    public static bool IsAuthorized(HttpContext httpContext, string apiKey)
    {
        return httpContext.Request.Headers.TryGetValue("X-API-Key", out var providedKey)
            && string.Equals(providedKey, apiKey, StringComparison.Ordinal);
    }
}

record SeatDeltaRequest(int Delta);
