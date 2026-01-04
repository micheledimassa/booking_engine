using flight_booking.Models;

namespace flight_booking.Repositories;

public interface IFlightInventoryRepository
{
    Task EnsureDepartureAvailabilityAsync(BookingPayload payload, CancellationToken cancellationToken);
    Task ApplySeatDeltaAsync(Guid? partenzaSyncId, int delta, CancellationToken cancellationToken);
}
