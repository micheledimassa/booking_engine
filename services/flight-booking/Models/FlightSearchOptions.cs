namespace flight_booking.Models;

public sealed class FlightSearchOptions
{
    public required string BaseUrl { get; init; }
    public required string ApiKey { get; init; }
}
