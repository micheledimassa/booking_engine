using System.Linq;
using Npgsql;

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

// === HEALTH ===
app.MapGet("/health", async () =>
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    return Results.Ok(new { status = "ok", db = "connected" });
});

// === ANDATA/RITORNO ===
app.MapGet("/flights/andataritorno", async (string from_iata, string to_iata) =>
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
        SELECT id, gruppo, direzione, volo_id, compagnia,
               data, ora, posti_totali, posti_disponibili,
               prezzo, is_open
        FROM search_flight
        WHERE (from_iata = @from AND to_iata = @to)
           OR (from_iata = @to   AND to_iata = @from)
        ORDER BY gruppo, direzione, data, ora NULLS LAST;
    ", conn);

    cmd.Parameters.AddWithValue("from", from);
    cmd.Parameters.AddWithValue("to", to);

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
            reader.GetGuid(idx.Id),
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
                    id = a.Id.ToString(),
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
                    id = r.Id.ToString(),
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
            name = (string?)null,
            nome = (string?)null,
            destinazione = (string?)null,
            paese = (string?)null,
            destinazione_nome = (string?)null
        },
        to = new
        {
            iata = to,
            name = (string?)null,
            nome = (string?)null,
            destinazione = (string?)null,
            paese = (string?)null,
            destinazione_nome = (string?)null
        }
    };

    return Results.Ok(new { route, voli });
});

app.MapPost("/sync/flight", async (HttpContext httpContext, FlightSync payload) =>
{
    if (!httpContext.Request.Headers.TryGetValue("X-API-Key", out var providedKey) ||
        !string.Equals(providedKey, apiKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    var deleteCmd = new NpgsqlCommand(@"
        DELETE FROM search_flight
        WHERE gruppo = @gruppo AND direzione = @direzione;
    ", conn, tx);
    deleteCmd.Parameters.AddWithValue("gruppo", payload.Gruppo);
    deleteCmd.Parameters.AddWithValue("direzione", payload.Direzione);
    await deleteCmd.ExecuteNonQueryAsync();

    // Inserisci il nuovo volo
    var insertCmd = new NpgsqlCommand(@"
        INSERT INTO search_flight (
            id,
            gruppo,
            direzione,
            volo_id,
            compagnia,
            from_iata,
            to_iata,
            data,
            ora,
            posti_totali,
            posti_disponibili,
            prezzo,
            is_open
        ) VALUES (
            @id, @gruppo, @direzione, @volo_id, @compagnia,
            @from_iata, @to_iata, @data, @ora,
            @posti_totali, @posti_disponibili, @prezzo, @is_open
        );
    ", conn, tx);

    insertCmd.Parameters.AddWithValue("id", payload.Id);
    insertCmd.Parameters.AddWithValue("gruppo", payload.Gruppo);
    insertCmd.Parameters.AddWithValue("direzione", payload.Direzione);
    insertCmd.Parameters.AddWithValue("volo_id", payload.Volo_Id);
    insertCmd.Parameters.AddWithValue("compagnia", payload.Compagnia ?? (object)DBNull.Value);
    insertCmd.Parameters.AddWithValue("from_iata", payload.From_Iata);
    insertCmd.Parameters.AddWithValue("to_iata", payload.To_Iata);
    insertCmd.Parameters.AddWithValue("data", payload.Data.ToDateTime(TimeOnly.MinValue));
    insertCmd.Parameters.AddWithValue("ora", payload.Ora is null ? DBNull.Value : TimeSpan.Parse(payload.Ora));
    insertCmd.Parameters.AddWithValue("posti_totali", payload.Posti_Totali);
    insertCmd.Parameters.AddWithValue("posti_disponibili", payload.Posti_Disponibili);
    insertCmd.Parameters.AddWithValue("prezzo", payload.Prezzo);
    insertCmd.Parameters.AddWithValue("is_open", payload.Is_Open);

    await insertCmd.ExecuteNonQueryAsync();
    await tx.CommitAsync();

    return Results.Ok(new { status = "synced" });
});


app.Run("http://0.0.0.0:8080");

// --- tipi sotto --- 
record FlightSegment(
    Guid Id,
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
