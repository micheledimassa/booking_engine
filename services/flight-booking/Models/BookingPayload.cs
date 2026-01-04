using flight_booking.Contracts;

namespace flight_booking.Models;

public sealed record BookingPayload
{
    public Guid Id { get; init; }
    public Guid? Partenza_Sync_Id { get; init; }
    public string? Partenza_Id { get; init; }
    public int DocStatus { get; init; }
    public string Stato { get; init; } = "Bozza";
    public string Canale { get; init; } = "Online";
    public int Posti { get; init; }
    public decimal Importo_Totale { get; init; }
    public string Valuta { get; init; } = "EUR";
    public string? DocName { get; init; }
    public string? Note { get; init; }
    public string? Gruppo { get; init; }
    public IReadOnlyList<PassengerDto> Passeggeri { get; init; } = Array.Empty<PassengerDto>();

    public static BookingPayload FromRequest(BookingWebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Partenza_Sync_Id is null && string.IsNullOrWhiteSpace(request.Partenza_Id))
            throw new ArgumentException("Partenza_Sync_Id o Partenza_Id è obbligatorio.");

        if (request.Importo_Totale is null || request.Importo_Totale <= 0)
            throw new ArgumentException("Importo_Totale deve essere maggiore di zero.");

        var normalizedChannel = NormalizeChannel(request.Canale);
        var passengers = request.Passeggeri?.ToList() ?? new List<PassengerDto>();
        var posti = request.Posti ?? passengers.Count;

        if (posti <= 0)
            throw new ArgumentException("Posti non può essere zero.");

        var docStatus = ResolveDocStatus(request.DocStatus, request.Stato);
        var stato = ResolveState(docStatus, request.Stato);

        return new BookingPayload
        {
            Id = request.Id,
            Partenza_Sync_Id = request.Partenza_Sync_Id,
            Partenza_Id = request.Partenza_Id,
            DocStatus = docStatus,
            Stato = stato,
            Canale = normalizedChannel,
            Posti = posti,
            Importo_Totale = request.Importo_Totale.Value,
            Valuta = string.IsNullOrWhiteSpace(request.Valuta) ? "EUR" : request.Valuta!,
            DocName = request.DocName,
            Note = request.Note,
            Gruppo = request.Gruppo,
            Passeggeri = passengers
        };
    }

    private static int ResolveDocStatus(int? docStatus, string? stato)
    {
        if (docStatus.HasValue)
        {
            if (docStatus.Value is < 0 or > 2)
                throw new ArgumentException("DocStatus valido: 0, 1 o 2.");
            return docStatus.Value;
        }

        if (string.IsNullOrWhiteSpace(stato))
            throw new ArgumentException("Fornire DocStatus o Stato.");

        return stato.Trim().ToLowerInvariant() switch
        {
            "bozza" => 0,
            "confermata" => 1,
            "cancellata" => 2,
            _ => throw new ArgumentException("Stato non riconosciuto. Valori ammessi: Bozza, Confermata, Cancellata.")
        };
    }

    private static string ResolveState(int docStatus, string? stato)
    {
        if (!string.IsNullOrWhiteSpace(stato))
            return Capitalize(stato);

        return docStatus switch
        {
            0 => "Bozza",
            1 => "Confermata",
            2 => "Cancellata",
            _ => "Bozza"
        };
    }

    private static string NormalizeChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Canale è obbligatorio.");

        var normalized = channel.Trim().ToLowerInvariant();
        return normalized switch
        {
            "online" => "Online",
            "agenzia" => "Agenzia",
            _ => throw new ArgumentException("Canale valido: Online o Agenzia.")
        };
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Trim();
        if (value.Length == 1) return value.ToUpperInvariant();
        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}
