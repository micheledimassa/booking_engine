using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using flight_booking.Models;

namespace flight_booking.Contracts;

public sealed record BookingWebhookRequest
{
    [Required]
    public Guid Id { get; init; }

    [JsonPropertyName("Partenza_Sync_Id")]
    public Guid? Partenza_Sync_Id { get; init; }

    [JsonPropertyName("Partenza_Id")]
    public string? Partenza_Id { get; init; }

    [JsonPropertyName("DocStatus")]
    public int? DocStatus { get; init; }

    [JsonPropertyName("Stato")]
    public string? Stato { get; init; }

    [Required]
    [JsonPropertyName("Canale")]
    public string? Canale { get; init; }

    [JsonPropertyName("Posti")]
    public int? Posti { get; init; }

    [Required]
    [JsonPropertyName("Importo_Totale")]
    public decimal? Importo_Totale { get; init; }

    [JsonPropertyName("Valuta")]
    public string? Valuta { get; init; }

    [JsonPropertyName("DocName")]
    public string? DocName { get; init; }

    [JsonPropertyName("Note")]
    public string? Note { get; init; }

    [JsonPropertyName("Gruppo")]
    public string? Gruppo { get; init; }

    [JsonPropertyName("Passeggeri")]
    public IReadOnlyList<PassengerDto>? Passeggeri { get; init; }
}
