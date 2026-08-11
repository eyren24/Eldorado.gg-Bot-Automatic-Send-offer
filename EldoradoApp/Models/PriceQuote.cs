namespace EldoradoApp.Models;

/// <summary>One line of a price breakdown ("Gold 3 · +0,75 €").</summary>
public sealed record PriceLine(string Label, decimal Amount, PriceLineKind Kind);

public enum PriceLineKind
{
    Base,
    Division,
    Unit,
    Extra,
    Adjustment
}

/// <summary>
/// The result of pricing one boosting request: the total to offer plus the full
/// breakdown, so the UI (and the activity log) can always explain the number.
/// </summary>
public sealed record PriceQuote(
    decimal Total,
    string Currency,
    string? FromRank,
    string? ToRank,
    IReadOnlyList<string> BilledDivisions,
    IReadOnlyList<PriceLine> Lines,
    string? Problem,
    BoostingCategoryKind Kind = BoostingCategoryKind.RankBoost,
    int Units = 0,
    string? Region = null)
{
    public bool IsPriceable => Problem is null && Total > 0;

    public int DivisionCount => BilledDivisions.Count;

    public string RangeText => Kind switch
    {
        BoostingCategoryKind.Placements => FromRank is null
            ? $"{Units} partite di placement"
            : $"Placement {ValorantRanks.Tier(FromRank)} · {Units} partite",
        BoostingCategoryKind.NetWins => FromRank is null
            ? $"{Units} net wins"
            : $"Net wins {ValorantRanks.Tier(FromRank)} · {Units} vittorie",
        BoostingCategoryKind.Flat => "prezzo fisso",
        _ => FromRank is null || ToRank is null
            ? "rank non riconosciuto"
            : $"{FromRank} → {ToRank}"
    };

    public string TotalText => $"{Total:N2} {Currency}";

    /// <summary>Compact one-liner for logs, e.g. "Gold 1 → Platinum 2 · 5 div · 14,45 EUR".</summary>
    public string Summary
    {
        get
        {
            if (!IsPriceable)
            {
                return $"{RangeText} · {Problem ?? "non quotabile"}";
            }

            var region = Region is null ? "" : $" · {Region}";
            var detail = Kind == BoostingCategoryKind.RankBoost ? $" · {DivisionCount} div" : "";
            return $"{RangeText}{detail}{region} · {TotalText}";
        }
    }

    public static PriceQuote Unpriceable(
        string currency,
        string? from,
        string? to,
        string problem,
        BoostingCategoryKind kind = BoostingCategoryKind.RankBoost) =>
        new(0m, currency, from, to, [], [], problem, kind);
}
