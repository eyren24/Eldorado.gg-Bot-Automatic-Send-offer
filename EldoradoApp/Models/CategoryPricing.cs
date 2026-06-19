namespace EldoradoApp.Models;

/// <summary>
/// Pricing for one boosting category (e.g. Valorant "Rank Boost", "Net Wins"). This is
/// the unit the API actually works with — a received request only carries its category,
/// not a rank range, so the bot offers a per-unit price + quantity range per category.
/// </summary>
public sealed class CategoryPricing
{
    public string GameId { get; set; } = "";
    public string CategoryId { get; set; } = "";

    /// <summary>Human-readable category name, for display (e.g. "Rank Boost").</summary>
    public string CategoryName { get; set; } = "";

    /// <summary>Whether the bot answers requests of this category.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Offered price per unit (per division/win/match, depending on the category).</summary>
    public decimal PricePerUnit { get; set; }

    /// <summary>Maximum units offered (the buyer can pick within [MinQuantity..Quantity]).</summary>
    public int Quantity { get; set; } = 1;

    public int MinQuantity { get; set; } = 1;

    public BoostingDeliveryTime DeliveryTime { get; set; } = BoostingDeliveryTime.Day1;

    public string Key => $"{GameId}:{CategoryId}";
}
