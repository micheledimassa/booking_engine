namespace flight_booking.Models;

public sealed record BookingAcceptedResponse
{
    public Guid BookingId { get; init; }
    public string Status { get; init; } = "RECEIVED";
}
