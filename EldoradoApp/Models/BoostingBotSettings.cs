namespace EldoradoApp.Models;

/// <summary>
/// Configuration for the auto-offer bot. Pricing is keyed by boosting <b>category</b>
/// (e.g. "Rank Boost", "Net Wins") because that's all a received request exposes — there
/// is no rank range in the API. Defaults are safe: disarmed (<see cref="Enabled"/>=false)
/// and in <see cref="DryRun"/> mode.
/// </summary>
public sealed class BoostingBotSettings
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 15;
    public string Currency { get; set; } = "USD";

    /// <summary>Restrict polling to a single game (selected from the dropdown). Null = all games.</summary>
    public string? GameId { get; set; }

    /// <summary>Custom note shown alongside an offer (editable any time).</summary>
    public string OfferMessage { get; set; } = "";

    /// <summary>Per-category pricing across all games the seller configured.</summary>
    public List<CategoryPricing> CategoryPrices { get; set; } = [];

    /// <summary>Regions/servers to accept. Empty = accept all (region isn't exposed by the API).</summary>
    public List<string> AcceptedRegions { get; set; } = [];

    /// <summary>Pricing for a specific game+category, or null if not configured.</summary>
    public CategoryPricing? ForCategory(string? gameId, string? categoryId) =>
        gameId is null || categoryId is null
            ? null
            : CategoryPrices.FirstOrDefault(c =>
                string.Equals(c.GameId, gameId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the region is accepted (empty accept-list = accept everything).</summary>
    public bool IsRegionAccepted(string? region)
    {
        if (AcceptedRegions.Count == 0)
        {
            return true;
        }

        return region is not null
               && AcceptedRegions.Any(r => string.Equals(r.Trim(), region, StringComparison.OrdinalIgnoreCase));
    }

    public static BoostingBotSettings CreateDefault() => new();
}
