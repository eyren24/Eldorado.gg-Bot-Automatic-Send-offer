using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>Server-side filter for the received boosting requests feed.</summary>
public enum BoostingRequestFilter
{
    ActiveRequests,
    OfferSubmitted,
    OfferWon,
    OfferLost
}

/// <summary>
/// Reads the boosting requests routed to the authenticated seller
/// (<c>GET /api/boostingOffers/me/boostingRequests/received</c>).
/// </summary>
public interface IBoostingRequestClient
{
    Task<IReadOnlyList<BoostingRequest>> GetReceivedRequestsAsync(
        BoostingRequestFilter filter = BoostingRequestFilter.ActiveRequests,
        string? gameId = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default);
}
