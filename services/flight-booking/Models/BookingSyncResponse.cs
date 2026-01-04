using System.Text.Json.Serialization;

namespace flight_booking.Models;

public sealed record BookingSyncResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("uuid")]
    public Guid Uuid { get; init; }

    [JsonPropertyName("docstatus")]
    public int DocStatus { get; init; }
}
