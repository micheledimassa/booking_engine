namespace flight_booking.Models;

public sealed class FrappeOptions
{
    public const string DefaultEndpoint = "/api/method/tour_operator.api.booking_webhook.receive_booking";

    public required string BaseUrl { get; init; }
    public required string ApiKey { get; init; }
    public string Endpoint { get; init; } = DefaultEndpoint;
}
