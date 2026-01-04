using flight_booking.Models;

namespace flight_booking.Services;

public interface IFlightInventoryClient
{
    Task EnsureDepartureAvailabilityAsync(Guid partenzaId, int postiRichiesti, CancellationToken cancellationToken);
    Task ApplySeatDeltaAsync(Guid partenzaId, int delta, CancellationToken cancellationToken);
}
