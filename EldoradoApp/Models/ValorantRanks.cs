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
