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
    /// <param name="hydrate">
    /// Also fetch each request's form answers (rank range, server, game count) — needed to
    /// price a request, pointless for a list that is only being counted.
    /// </param>
    Task<IReadOnlyList<BoostingRequest>> GetReceivedRequestsAsync(
        BoostingRequestFilter filter = BoostingRequestFilter.ActiveRequests,
        string? gameId = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default,
        bool hydrate = true);
}
