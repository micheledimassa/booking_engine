using flight_booking.Models;

namespace flight_booking.Repositories;

public interface IBookingRepository
{
    Task<BookingRecord?> UpsertAsync(BookingPayload payload, CancellationToken cancellationToken);
    Task MarkSyncedAsync(Guid bookingId, BookingSyncResponse response, CancellationToken cancellationToken);
    Task<BookingRecord?> GetAsync(Guid bookingId, CancellationToken cancellationToken);
}
