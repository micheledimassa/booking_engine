using System.Text.Json.Serialization;

namespace flight_booking.Models;

public sealed record PassengerDto
{
    [JsonPropertyName("Nome")]
    public string? Nome { get; init; }

    [JsonPropertyName("Cognome")]
    public string? Cognome { get; init; }

    [JsonPropertyName("Data_Nascita")]
    public string? Data_Nascita { get; init; }

    [JsonPropertyName("Documento")]
    public string? Documento { get; init; }

    [JsonPropertyName("Note")]
    public string? Note { get; init; }
}
