namespace EldoradoApp.Models;

/// <summary>
/// Everything the auto-offer bot needs, all of it editable from the UI.
/// <para>
/// Pricing is a two-layer model: the <see cref="Pricing"/> rank ladder produces the
/// number (base price + one surcharge per division climbed), and <see cref="Extras"/>
/// adds buyer-requested options (DuoQ, streaming, …). <see cref="CategoryPrices"/>
/// survives only to say <i>which</i> categories the bot answers and with what delivery
/// time — plus an optional flat override when a category can't be priced by rank.
/// </para>
/// Defaults are safe: disarmed (<see cref="Enabled"/>=false) and in <see cref="DryRun"/>.
/// </summary>
public sealed class BoostingBotSettings
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 15;
    public string Currency { get; set; } = "EUR";

    /// <summary>Restrict polling to a single game (selected from the dropdown). Null = all games.</summary>
    public string? GameId { get; set; }

    /// <summary>Rank-ladder price list: base price + per-division surcharges.</summary>
    public RankPricing Pricing { get; set; } = RankPricing.CreateDefault();

    /// <summary>Placement matches: base + price per game, by last season's tier.</summary>
    public UnitPricing Placements { get; set; } = UnitPricing.CreatePlacementsDefault();

    /// <summary>Net wins: base + price per win, by the player's current tier.</summary>
    public UnitPricing NetWins { get; set; } = UnitPricing.CreateNetWinsDefault();

    /// <summary>Per-region accept switch and price multiplier.</summary>
    public List<RegionRule> Regions { get; set; } = RegionRule.CreateDefaults();

    /// <summary>Answer every region, ignoring the per-region accept switches.</summary>
    public bool AcceptAllRegions { get; set; }

    /// <summary>Answer requests whose region couldn't be read from the text.</summary>
    public bool AcceptUnknownRegion { get; set; } = true;

    /// <summary>
    /// Only ever work on Valorant. The game is resolved from the seller's subscriptions,
    /// so the bot never answers requests for another title by accident.
    /// </summary>
    public bool ValorantOnly { get; set; } = true;

    /// <summary>Surcharges for buyer options detected in the request (DuoQ, stream, …).</summary>
    public List<ExtraOption> Extras { get; set; } = ExtraOption.CreateDefaults();

    /// <summary>The message + banner fired right after an offer is submitted.</summary>
    public OfferMessageSettings Message { get; set; } = new();

    /// <summary>Optional ASP.NET Core control plane for subscriptions, licences and safe settings backup.</summary>
    public RemoteControlSettings RemoteControl { get; set; } = new();

    /// <summary>Default delivery time when a category has no specific one.</summary>
    public BoostingDeliveryTime DefaultDeliveryTime { get; set; } = BoostingDeliveryTime.Day1;

    /// <summary>Per-category switches: on/off, delivery time and optional flat price.</summary>
    public List<CategoryPricing> CategoryPrices { get; set; } = [];

    /// <summary>
    /// Answer requests whose category isn't in <see cref="CategoryPrices"/> yet.
    /// On by default so a newly subscribed category doesn't silently go unanswered.
    /// </summary>
    public bool AnswerUnknownCategories { get; set; } = true;

    /// <summary>Skip requests whose rank range can't be parsed instead of using the base price.</summary>
    public bool SkipUnparsableRanges { get; set; } = true;

    /// <summary>Legacy free-text accept list, migrated into <see cref="Regions"/>.</summary>
    public List<string> AcceptedRegions { get; set; } = [];

    /// <summary>Pricing for a specific game+category, or null if not configured.</summary>
    public CategoryPricing? ForCategory(string? gameId, string? categoryId) =>
        gameId is null || categoryId is null
            ? null
            : CategoryPrices.FirstOrDefault(c =>
                string.Equals(c.GameId, gameId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the bot should answer requests from this region.</summary>
    public bool IsRegionAccepted(string? region)
    {
        if (AcceptAllRegions)
        {
            return true;
        }

        if (region is null)
        {
            return AcceptUnknownRegion;
        }

        var rule = RegionFor(region);
        return rule?.Accepted ?? AcceptUnknownRegion;
    }

    /// <summary>Price factor for a region; 1 when unknown or not configured.</summary>
    public decimal RegionMultiplier(string? region)
    {
        var rule = region is null ? null : RegionFor(region);
        return rule is { Multiplier: > 0 } ? rule.Multiplier : 1m;
    }

    public RegionRule? RegionFor(string region) =>
        Regions.FirstOrDefault(r => string.Equals(r.Code, region, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How a category is priced: what the seller set on the row, or a guess from its name
    /// so a freshly discovered category is handled sensibly before anyone configures it.
    /// </summary>
    public BoostingCategoryKind KindFor(string? gameId, string? categoryId, string? title)
    {
        var configured = ForCategory(gameId, categoryId);
        if (configured is not null && configured.Kind != BoostingCategoryKind.RankBoost)
        {
            return configured.Kind;
        }

        var text = $"{configured?.CategoryName} {title}";
        if (text.Contains("placement", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("piazzament", StringComparison.OrdinalIgnoreCase))
        {
            return BoostingCategoryKind.Placements;
        }

        if (text.Contains("net win", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("netwin", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("wins", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("vittorie", StringComparison.OrdinalIgnoreCase))
        {
            return BoostingCategoryKind.NetWins;
        }

        return configured?.Kind ?? BoostingCategoryKind.RankBoost;
    }

    /// <summary>Delivery time for a category, falling back to the global default.</summary>
    public BoostingDeliveryTime DeliveryTimeFor(string? gameId, string? categoryId) =>
        ForCategory(gameId, categoryId)?.DeliveryTime ?? DefaultDeliveryTime;

    /// <summary>
    /// Repairs a settings file written by an older build (or hand-edited): makes sure the
    /// price list covers the ladder and that nothing required is null.
    /// </summary>
    public BoostingBotSettings Normalized()
    {
        Pricing ??= RankPricing.CreateDefault();
        Pricing.Ladder ??= new RankLadder();
        if (Pricing.Ladder.Divisions.Count == 0)
        {
            Pricing.Ladder.Divisions = [.. RankLadder.DefaultValorant()];
        }

        Pricing.Divisions ??= [];
        Pricing.SyncWithLadder();

        if (Pricing.Multiplier <= 0)
        {
            Pricing.Multiplier = 1m;
        }

        Placements ??= UnitPricing.CreatePlacementsDefault();
        NetWins ??= UnitPricing.CreateNetWinsDefault();
        Placements.SyncWithTiers();
        NetWins.SyncWithTiers();

        Extras ??= [];
        Message ??= new OfferMessageSettings();
        Message.Playwright ??= new PlaywrightMessageOptions();
        Message.Playwright.Normalize();
        RemoteControl ??= new RemoteControlSettings();
        RemoteControl.Normalize();
        CategoryPrices ??= [];
        AcceptedRegions ??= [];

        if (Regions is null || Regions.Count == 0)
        {
            Regions = RegionRule.CreateDefaults();

            // Carry over an accept-list written by the free-text build.
            if (AcceptedRegions.Count > 0)
            {
                foreach (var rule in Regions)
                {
                    rule.Accepted = AcceptedRegions.Any(r =>
                        string.Equals(r.Trim(), rule.Code, StringComparison.OrdinalIgnoreCase));
                }

                AcceptAllRegions = false;
            }
        }

        foreach (var rule in Regions.Where(r => r.Multiplier <= 0))
        {
            rule.Multiplier = 1m;
        }

        if (string.IsNullOrWhiteSpace(Currency))
        {
            Currency = "EUR";
        }

        return this;
    }

    public static BoostingBotSettings CreateDefault() => new();
}
