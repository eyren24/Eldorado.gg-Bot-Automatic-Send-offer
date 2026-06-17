namespace EldoradoApp.Models;

/// <summary>
/// A boosting job a buyer posted that was routed to this seller (the
/// <c>boostingRequests/received</c> feed). Unlike <see cref="BoostingOffer"/>
/// — which models a competitor's listing — a request is work to bid on.
/// </summary>
public sealed record BoostingRequest(
    string Id,
    string? GameId,
    string? BoostingCategoryId,
    string? BoostingCategoryTitle,
    string? BuyerId,
    string? BuyerUsername,
    bool IsBuyerMuted,
    DateTimeOffset CreatedDate);
