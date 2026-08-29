namespace EldoradoApp.Models;

/// <summary>
/// A boosting job a buyer posted that was routed to this seller (the
/// <c>boostingRequests/received</c> feed) — work to bid on.
/// </summary>
/// <remarks>
/// The feed itself is thin: it carries the category label and the buyer, nothing else.
/// The rank range, server and game count live on the request's <b>form answers</b>, which
/// is what <see cref="Facts"/> holds once the request has been hydrated from
/// <c>boostingRequests/{id}/details</c>. <see cref="RawJson"/> keeps the untouched feed
/// payload for the inspector, and parsing still falls back to it when hydration failed.
/// </remarks>
public sealed record BoostingRequest(
    string Id,
    string? GameId,
    string? BoostingCategoryId,
    string? BoostingCategoryTitle,
    string? BuyerId,
    string? BuyerUsername,
    bool IsBuyerMuted,
    DateTimeOffset CreatedDate,
    string? RawJson = null,
    BoostingRequestFacts? Facts = null,
    string? DetailsJson = null)
{
    /// <summary>Everything textual about the request, for rank/extra detection.</summary>
    public string SearchText => string.Join(" \n ", new[]
        {
            BoostingCategoryTitle, Facts?.Text, DetailsJson, RawJson
        }
        .Where(s => !string.IsNullOrWhiteSpace(s)));
}
