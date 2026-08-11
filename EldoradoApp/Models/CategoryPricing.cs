namespace EldoradoApp.Models;

/// <summary>
/// Per-category switches for one boosting category (e.g. Valorant "Rank Boost").
/// Since the price now comes from the rank ladder, this row decides <i>whether</i>
/// the bot answers the category, how fast it promises delivery, and — for categories
/// that have no rank range at all (Net Wins, Placements) — an optional flat price.
/// </summary>
public sealed class CategoryPricing
{
    public string GameId { get; set; } = "";
    public string CategoryId { get; set; } = "";

    /// <summary>Human-readable category name, for display (e.g. "Rank Boost").</summary>
    public string CategoryName { get; set; } = "";

    /// <summary>Whether the bot answers requests of this category.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How this category is priced. Left at <see cref="BoostingCategoryKind.RankBoost"/>
    /// the app guesses from the category name (see <c>BoostingBotSettings.KindFor</c>).
    /// </summary>
    public BoostingCategoryKind Kind { get; set; } = BoostingCategoryKind.RankBoost;

    /// <summary>
    /// Flat price used instead of the rank ladder when &gt; 0. Meant for categories
    /// without a rank range; leave at 0 to price by ladder.
    /// </summary>
    public decimal FlatPrice { get; set; }

    /// <summary>Maximum units offered (the buyer can pick within [MinQuantity..Quantity]).</summary>
    public int Quantity { get; set; } = 1;

    public int MinQuantity { get; set; } = 1;

    public BoostingDeliveryTime DeliveryTime { get; set; } = BoostingDeliveryTime.Day1;

    /// <summary>Legacy field from the per-unit pricing model; migrated into <see cref="FlatPrice"/>.</summary>
    public decimal PricePerUnit { get; set; }

    public string Key => $"{GameId}:{CategoryId}";
}
