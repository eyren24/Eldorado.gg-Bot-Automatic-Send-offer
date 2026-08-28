namespace EldoradoApp.Models;

/// <summary>Price charged for climbing into one division of the ladder.</summary>
public sealed class DivisionPrice
{
    public string Division { get; set; } = "";
    public decimal Price { get; set; }
}

/// <summary>How the computed total is mapped onto Eldorado's per-unit offer pricing.</summary>
public enum OfferPriceMode
{
    /// <summary>One unit = the whole boost; the total goes into <c>pricePerUnit</c>.</summary>
    TotalAsSingleUnit,

    /// <summary>One unit = one division; <c>pricePerUnit</c> = total ÷ divisions.</summary>
    PerDivision
}

/// <summary>
/// The rank-ladder price list: a flat <see cref="BasePrice"/> for every job plus a
/// per-division surcharge. A boost costs <c>BasePrice + Σ price(division)</c> over
/// every division the booster climbs into (see <see cref="RankLadder.Steps"/>).
/// </summary>
public sealed class RankPricing
{
    /// <summary>Flat amount added to every offer (e.g. 10 €).</summary>
    public decimal BasePrice { get; set; } = 10m;

    /// <summary>
    /// Charge the division the buyer starts from as well. On: <c>Gold 1 → Gold 3</c>
    /// bills Gold 1 + Gold 2 + Gold 3. Off: it bills Gold 2 + Gold 3 only.
    /// </summary>
    public bool IncludeStartDivision { get; set; } = true;

    /// <summary>The ladder these prices refer to (editable so it fits any game/naming).</summary>
    public RankLadder Ladder { get; set; } = new();

    /// <summary>Per-division surcharges. Divisions missing here cost 0.</summary>
    public List<DivisionPrice> Divisions { get; set; } = [];

    /// <summary>Never offer below this amount (0 = no floor).</summary>
    public decimal MinimumPrice { get; set; }

    /// <summary>Round the final total up to this step, e.g. 0.50. 0 = no rounding.</summary>
    public decimal RoundTo { get; set; }

    /// <summary>Global multiplier applied after divisions and before extras, 1 = off.</summary>
    public decimal Multiplier { get; set; } = 1m;

    public OfferPriceMode PriceMode { get; set; } = OfferPriceMode.TotalAsSingleUnit;

    public decimal PriceOf(string division) =>
        Divisions.FirstOrDefault(d =>
            string.Equals(d.Division, division, StringComparison.OrdinalIgnoreCase))?.Price ?? 0m;

    /// <summary>
    /// Adds a zero-priced row for every ladder division that has none yet, reusing the
    /// existing <see cref="DivisionPrice"/> objects. Reuse is not an optimisation: the
    /// price-list rows in the UI write straight through to these instances, so replacing
    /// them here (this runs on every save and on every bot cycle) would silently detach
    /// the editor and every later price change would be lost.
    /// </summary>
    public void SyncWithLadder()
    {
        var known = new Dictionary<string, DivisionPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Divisions)
        {
            known.TryAdd(row.Division, row);
        }

        var synced = new List<DivisionPrice>(Ladder.Divisions.Count);
        foreach (var name in Ladder.Divisions)
        {
            if (known.Remove(name, out var existing))
            {
                existing.Division = name;   // adopt the ladder's spelling
                synced.Add(existing);
            }
            else
            {
                synced.Add(new DivisionPrice { Division = name });
            }
        }

        Divisions = synced;
    }

    /// <summary>A sane starting list: cheap in low elo, steeply more expensive near the top.</summary>
    public static RankPricing CreateDefault()
    {
        var pricing = new RankPricing();
        pricing.SyncWithLadder();

        foreach (var row in pricing.Divisions)
        {
            row.Price = ValorantRanks.Tier(row.Division) switch
            {
                "Iron" => 0.25m,
                "Bronze" => 0.35m,
                "Silver" => 0.50m,
                "Gold" => 0.75m,
                "Platinum" => 1.10m,
                "Diamond" => 1.75m,
                "Ascendant" => 2.75m,
                "Immortal" => 4.50m,
                "Radiant" => 12.00m,
                _ => 1.00m
            };
        }

        return pricing;
    }
}
