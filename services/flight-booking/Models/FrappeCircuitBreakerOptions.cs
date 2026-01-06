namespace flight_booking.Models;

public sealed class FrappeCircuitBreakerOptions
{
    public const string SectionName = "FrappeCircuitBreaker";

    public int HandledEventsAllowedBeforeBreaking { get; set; } = 5;
    public int BreakDurationSeconds { get; set; } = 30;
}
