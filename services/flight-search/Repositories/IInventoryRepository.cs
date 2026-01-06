namespace flight_search.Repositories;

public interface IInventoryRepository
{
    Task<SeatDeltaResult> ApplySeatDeltaAsync(Guid partenzaId, int delta, CancellationToken cancellationToken);
}

public sealed record SeatDeltaResult(bool Success, int? PostiResidui, string? Reason);
