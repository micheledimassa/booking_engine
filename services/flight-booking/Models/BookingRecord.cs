namespace flight_booking.Models;

public sealed record BookingRecord
{
    public Guid Id { get; init; }
    public Guid? PartenzaSyncId { get; init; }
    public string? PartenzaId { get; init; }
    public int DocStatus { get; init; }
    public string Stato { get; init; } = "Bozza";
    public string Canale { get; init; } = string.Empty;
    public int Posti { get; init; }
    public decimal ImportoTotale { get; init; }
    public string Valuta { get; init; } = "EUR";
    public string? DocName { get; init; }
    public string? Note { get; init; }
    public string? Gruppo { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastSyncedAt { get; init; }
}
