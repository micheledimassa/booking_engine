using System.Net;
using System.Net.Http.Json;
using flight_booking.Models;

namespace flight_booking.Services;

public sealed class FlightInventoryClient : IFlightInventoryClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FlightInventoryClient> _logger;

    public FlightInventoryClient(HttpClient httpClient, ILogger<FlightInventoryClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task EnsureDepartureAvailabilityAsync(Guid partenzaId, int postiRichiesti, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"internal/flights/{partenzaId}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Partenza non trovata nel servizio flight-search.");

        response.EnsureSuccessStatusCode();

        var availability = await response.Content.ReadFromJsonAsync<FlightAvailabilityDto>(cancellationToken: cancellationToken);

        if (availability is null)
            throw new InvalidOperationException("Risposta flight-search non valida.");

        if (!availability.IsOpen)
            throw new InvalidOperationException("La partenza risulta chiusa a nuove prenotazioni.");

        if (availability.PostiDisponibili < postiRichiesti)
            throw new InvalidOperationException("Posti insufficienti per completare la prenotazione.");
    }

    public async Task ApplySeatDeltaAsync(Guid partenzaId, int delta, CancellationToken cancellationToken)
    {
        if (delta == 0)
            return;

        var response = await _httpClient.PostAsJsonAsync($"internal/flights/{partenzaId}/seat-delta", new { delta }, cancellationToken);

        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "Impossibile aggiornare i posti disponibili."
                : detail);
        }

        _logger.LogWarning("flight-search seat delta fallita con status {Status}", response.StatusCode);
        response.EnsureSuccessStatusCode();
    }
}
