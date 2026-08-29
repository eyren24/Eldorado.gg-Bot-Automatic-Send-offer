namespace EldoradoApp.Models;

/// <summary>
/// Valorant competitive tiers and their brand-ish colors, used for badges.
/// </summary>
public static class ValorantRanks
{
    public static readonly IReadOnlyList<string> Tiers =
    [
        "Iron", "Bronze", "Silver", "Gold", "Platinum",
        "Diamond", "Ascendant", "Immortal", "Radiant"
    ];

    /// <summary>
    /// A player with no rank yet — the state everyone starts a season in.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> part of <see cref="Tiers"/>: it has no place on the climbing
    /// ladder (you never boost <i>to</i> Unranked, and Eldorado's rank-boost form doesn't
    /// offer it). It is a tier of its own for placements, where it is the most common
    /// starting point — and not the cheapest one, because what an Unranked account places
    /// into is decided by Valorant's hidden MMR: the same "Unranked" can be a Gold or a
    /// Platinum underneath. It therefore gets its own per-game price, never Iron's.
    /// </remarks>
    public const string Unranked = "Unranked";

    /// <summary>
    /// Tiers a per-game category (placements, net wins) can be priced on: the ladder
    /// tiers plus <see cref="Unranked"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> UnitTiers = [Unranked, .. Tiers];

    /// <summary>True when a rank is the season-start "no rank yet" state.</summary>
    public static bool IsUnranked(string? rank) =>
        rank is not null && rank.Trim().Replace("-", "").Replace(" ", "")
            .Equals("unranked", StringComparison.OrdinalIgnoreCase);

    public static string Tier(string rank)
    {
        if (string.IsNullOrWhiteSpace(rank))
        {
            return "Iron";
        }

        var space = rank.IndexOf(' ');
        return space < 0 ? rank : rank[..space];
    }

    public static string ColorHex(string rank) => Tier(rank) switch
    {
        Unranked => "#8E9AAF",
        "Iron" => "#6E7174",
        "Bronze" => "#A1764B",
        "Silver" => "#C4CACE",
        "Gold" => "#E6C200",
        "Platinum" => "#3FC1C9",
        "Diamond" => "#C792EA",
        "Ascendant" => "#1FA463",
        "Immortal" => "#C9385A",
        "Radiant" => "#FFF4B8",
        _ => "#6E7174"
    };

    /// <summary>
    /// Every competitive division in climbing order: Iron 1..3, Bronze 1..3, … Immortal 1..3, Radiant.
    /// </summary>
    public static readonly IReadOnlyList<string> Divisions = BuildDivisions();

    private static string[] BuildDivisions()
    {
        var list = new List<string>();
        foreach (var tier in Tiers)
        {
            if (tier == "Radiant")
            {
                list.Add(tier);
            }
            else
            {
                list.Add($"{tier} 1");
                list.Add($"{tier} 2");
                list.Add($"{tier} 3");
            }
        }

        return list.ToArray();
    }

    /// <summary>Global index of a rank within <see cref="Divisions"/>; falls back to the tier start.</summary>
    public static int DivisionIndex(string rank)
    {
        var exact = Array.IndexOf((string[])Divisions, rank);
        if (exact >= 0)
        {
            return exact;
        }

        var tier = Tier(rank);
        for (var i = 0; i < Divisions.Count; i++)
        {
            if (Tier(Divisions[i]) == tier)
            {
                return i;
            }
        }

        return 0;
    }
}
