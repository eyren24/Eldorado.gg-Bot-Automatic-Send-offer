namespace EldoradoApp.Models;

/// <summary>
/// What the buyer actually filled in on the request form, read from
/// <c>GET /api/boostingOffers/boostingRequests/{id}/details</c> and named via the
/// category's form schema (<see cref="BoostingFormConfig"/>).
/// </summary>
/// <remarks>
/// This is the reliable source for the rank range. The received-requests feed only
/// carries the category label ("Rank Boost", "Net Wins", …), which is why parsing the
/// title alone could never recognise a rank — the ranks live here, one form answer per
/// input id: 26 "Current Rank", 53 "Desired Rank", 60 "Server" for Valorant rank boosts.
/// </remarks>
public sealed record BoostingRequestFacts(
    string? CurrentRank,
    string? DesiredRank,
    string? Server,
    int? Quantity,
    string? Notes,
    IReadOnlyList<string> Toggles)
{
    public static readonly BoostingRequestFacts Empty = new(null, null, null, null, null, []);

    public bool HasAnything =>
        CurrentRank is not null || DesiredRank is not null || Server is not null ||
        Quantity is not null || !string.IsNullOrWhiteSpace(Notes) || Toggles.Count > 0;

    /// <summary>Everything the buyer wrote or picked, for keyword matching (extras).</summary>
    public string Text => string.Join(" \n ", new[]
        {
            CurrentRank, DesiredRank, Server, Notes, string.Join(" ", Toggles)
        }
        .Where(s => !string.IsNullOrWhiteSpace(s)));
}
