namespace EldoradoApp.Models;

/// <summary>
/// Configuration for the auto-offer bot. Price and delivery time are computed
/// per division/rank (like a tiered price list), so the bot can answer any
/// parsed rank range. Defaults are safe: disarmed (<see cref="Enabled"/>=false)
/// and in <see cref="DryRun"/> mode.
/// </summary>
public sealed class BoostingBotSettings
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 15;
    public string Currency { get; set; } = "USD";

    /// <summary>Restrict polling to a single game (Valorant). Null = all games.</summary>
    public string? GameId { get; set; }

    /// <summary>Base fee added to every computed offer.</summary>
    public decimal FlatFee { get; set; }

    /// <summary>Price charged per division climbed, keyed by tier (Iron, Bronze, …).</summary>
    public Dictionary<string, decimal> PricePerDivisionByTier { get; set; } = new();

    /// <summary>Estimated hours of work per division climbed, keyed by tier.</summary>
    public Dictionary<string, double> HoursPerDivisionByTier { get; set; } = new();

    /// <summary>Regions/servers to accept (e.g. "EU", "NA"). Empty = accept all.</summary>
    public List<string> AcceptedRegions { get; set; } = [];

    /// <summary>Tiers to refuse (e.g. "Immortal", "Radiant").</summary>
    public List<string> ExcludedRanks { get; set; } = [];

    public decimal PricePerDivision(string tier) =>
        PricePerDivisionByTier.TryGetValue(tier, out var v) ? v : 0m;

    public double HoursPerDivision(string tier) =>
        HoursPerDivisionByTier.TryGetValue(tier, out var v) ? v : 0d;

    public bool IsRankExcluded(string? tier) =>
        tier is not null && ExcludedRanks.Any(r => string.Equals(r.Trim(), tier, StringComparison.OrdinalIgnoreCase));

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

    public static BoostingBotSettings CreateDefault()
    {
        var settings = new BoostingBotSettings();
        foreach (var tier in ValorantRanks.Tiers)
        {
            settings.PricePerDivisionByTier[tier] = 2.0m;
            settings.HoursPerDivisionByTier[tier] = 2.0;
        }

        return settings;
    }

    /// <summary>Ensures every tier has a price/hours entry (e.g. after loading older JSON).</summary>
    public void EnsureTierEntries()
    {
        foreach (var tier in ValorantRanks.Tiers)
        {
            PricePerDivisionByTier.TryAdd(tier, 2.0m);
            HoursPerDivisionByTier.TryAdd(tier, 2.0);
        }
    }
}
