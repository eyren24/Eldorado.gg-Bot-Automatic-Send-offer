namespace EldoradoApp.Models;

/// <summary>
/// A boosting job a buyer posted that was routed to this seller (the
/// <c>boostingRequests/received</c> feed) — work to bid on.
/// </summary>
/// <remarks>
/// <see cref="RawJson"/> keeps the untouched server payload: the documented DTO only
/// exposes the category title, but live responses may carry the rank range, region and
/// buyer options under names we haven't mapped. Parsing falls back to it, and the
/// request inspector in the UI shows it verbatim so the parser can be calibrated.
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
    string? RawJson = null)
{
    /// <summary>Everything textual about the request, for rank/extra detection.</summary>
    public string SearchText => string.Join(" \n ", new[] { BoostingCategoryTitle, RawJson }
        .Where(s => !string.IsNullOrWhiteSpace(s)));
}
