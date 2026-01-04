using flight_booking.Contracts;
using flight_booking.Models;
using flight_booking.Repositories;

namespace flight_booking.Services;

public sealed class BookingService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IFlightInventoryClient _flightInventoryClient;
    private readonly FrappeBookingClient _frappeClient;
    private readonly ILogger<BookingService> _logger;

    public BookingService(
        IBookingRepository bookingRepository,
        IFlightInventoryClient flightInventoryClient,
        FrappeBookingClient frappeClient,
        ILogger<BookingService> logger)
    {
        _bookingRepository = bookingRepository;
        _flightInventoryClient = flightInventoryClient;
        _frappeClient = frappeClient;
        _logger = logger;
    }

    public async Task<BookingSyncResponse> HandleAsync(BookingWebhookRequest request, CancellationToken cancellationToken)
    {
        var payload = BookingPayload.FromRequest(request);

        if (payload.Partenza_Sync_Id is null)
            throw new InvalidOperationException("Partenza_Sync_Id è obbligatoria per la prenotazione.");

        await _flightInventoryClient.EnsureDepartureAvailabilityAsync(payload.Partenza_Sync_Id.Value, payload.Posti, cancellationToken);
        var previous = await _bookingRepository.UpsertAsync(payload, cancellationToken);

        BookingSyncResponse? response = null;

        try
        {
            response = await _frappeClient.UpsertBookingAsync(payload, cancellationToken);
            var delta = ComputeSeatDelta(previous, payload, response);
            await _flightInventoryClient.ApplySeatDeltaAsync(payload.Partenza_Sync_Id.Value, delta, cancellationToken);
            await _bookingRepository.MarkSyncedAsync(payload.Id, response, cancellationToken);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogError(ex, "Errore nel sincronizzare la prenotazione {BookingId}", payload.Id);
            if (response is not null)
                await _bookingRepository.MarkSyncedAsync(payload.Id, response, cancellationToken);
            throw;
        }
    }

    private static int ComputeSeatDelta(BookingRecord? previous, BookingPayload payload, BookingSyncResponse response)
    {
        var previousSeats = previous is not null && previous.DocStatus == 1 ? previous.Posti : 0;
        var newSeats = response.DocStatus == 1 ? payload.Posti : 0;
        return newSeats - previousSeats;
    }

}
