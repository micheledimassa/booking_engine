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

        const int docStatus = 0;
        const string stato = "Bozza";

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

}
