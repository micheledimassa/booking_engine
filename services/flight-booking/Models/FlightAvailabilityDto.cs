using System.Text.Json.Serialization;

namespace flight_booking.Models;

public sealed record FlightAvailabilityDto
{
    [JsonPropertyName("posti_disponibili")]
    public int PostiDisponibili { get; init; }

    [JsonPropertyName("is_open")]
    public bool IsOpen { get; init; }
}
