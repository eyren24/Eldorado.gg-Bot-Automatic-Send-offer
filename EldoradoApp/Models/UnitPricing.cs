namespace EldoradoApp.Models;

/// <summary>What kind of job a boosting category is, which decides how it's priced.</summary>
public enum BoostingCategoryKind
{
    /// <summary>Climb from rank A to rank B — priced on the division ladder.</summary>
    RankBoost,

    /// <summary>Placement matches — priced per game, by last season's rank.</summary>
    Placements,

    /// <summary>Net wins — priced per win, by the player's current rank.</summary>
    NetWins,

    /// <summary>Anything else: a single flat price set on the category row.</summary>
    Flat
}

/// <summary>Price charged per game/win for players of one tier.</summary>
public sealed class TierUnitPrice
{
    public string Tier { get; set; } = "";
    public decimal PricePerUnit { get; set; }
}

/// <summary>
/// Pricing for the "N games at rank X" categories — placements and net wins.
/// <code>totale = prezzoBase + prezzoPerPartita(tier) × numeroPartite</code>
/// The tier comes from the rank in the request (last season's rank for placements,
/// the current one for net wins) and the count is read from the request text, falling
/// back to <see cref="DefaultQuantity"/>.
/// </summary>
public sealed class UnitPricing
{
    public bool Enabled { get; set; } = true;

    /// <summary>Flat amount added to every order of this kind.</summary>
    public decimal BasePrice { get; set; }

    /// <summary>Games/wins assumed when the request doesn't say how many.</summary>
    public int DefaultQuantity { get; set; } = 5;

    /// <summary>Never bill more than this many units (0 = no cap).</summary>
    public int MaxQuantity { get; set; } = 30;

    /// <summary>Per-tier price for one game/win.</summary>
    public List<TierUnitPrice> Tiers { get; set; } = [];

    public decimal PriceOf(string? tier) =>
        tier is null
            ? 0m
            : Tiers.FirstOrDefault(t => string.Equals(t.Tier, tier, StringComparison.OrdinalIgnoreCase))?.PricePerUnit ?? 0m;

    /// <summary>
    /// Adds a row for every Valorant tier that doesn't have one yet, reusing the existing
    /// <see cref="TierUnitPrice"/> objects — the editor rows write straight through to
    /// them, so replacing them here would detach the editor (see
    /// <see cref="RankPricing.SyncWithLadder"/>).
    /// </summary>
    public void SyncWithTiers()
    {
        var known = new Dictionary<string, TierUnitPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Tiers)
        {
            known.TryAdd(row.Tier, row);
        }

        var synced = new List<TierUnitPrice>(ValorantRanks.Tiers.Count);
        foreach (var tier in ValorantRanks.Tiers)
        {
            if (known.Remove(tier, out var existing))
            {
                existing.Tier = tier;
                synced.Add(existing);
            }
            else
            {
                synced.Add(new TierUnitPrice { Tier = tier });
            }
        }

        Tiers = synced;
    }

    /// <summary>Clamps a requested game count into the allowed range.</summary>
    public int Clamp(int? quantity)
    {
        var value = quantity is > 0 ? quantity.Value : DefaultQuantity;
        return MaxQuantity > 0 ? Math.Min(value, MaxQuantity) : value;
    }

    private static UnitPricing Build(decimal basePrice, int defaultQuantity, Func<string, decimal> perGame)
    {
        var pricing = new UnitPricing { BasePrice = basePrice, DefaultQuantity = defaultQuantity };
        pricing.SyncWithTiers();

        foreach (var row in pricing.Tiers)
        {
            row.PricePerUnit = perGame(row.Tier);
        }

        return pricing;
    }

    /// <summary>Placement matches: 5 games by default, priced on last season's tier.</summary>
    public static UnitPricing CreatePlacementsDefault() => Build(5m, 5, tier => tier switch
    {
        "Iron" => 1.50m,
        "Bronze" => 1.80m,
        "Silver" => 2.20m,
        "Gold" => 2.80m,
        "Platinum" => 3.50m,
        "Diamond" => 4.50m,
        "Ascendant" => 6.00m,
        "Immortal" => 9.00m,
        "Radiant" => 15.00m,
        _ => 3.00m
    });

    /// <summary>Net wins: priced per win on the current tier.</summary>
    public static UnitPricing CreateNetWinsDefault() => Build(3m, 10, tier => tier switch
    {
        "Iron" => 1.20m,
        "Bronze" => 1.50m,
        "Silver" => 1.90m,
        "Gold" => 2.40m,
        "Platinum" => 3.20m,
        "Diamond" => 4.20m,
        "Ascendant" => 5.50m,
        "Immortal" => 8.00m,
        "Radiant" => 14.00m,
        _ => 2.50m
    });
}
