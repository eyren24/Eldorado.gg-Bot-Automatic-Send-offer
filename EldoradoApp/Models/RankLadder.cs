using System.Text.RegularExpressions;

namespace EldoradoApp.Models;

/// <summary>
/// The ordered ladder of competitive divisions used for pricing, lowest first.
/// Editable by the seller because Eldorado's category titles don't always match
/// the in-game names (and other games have different ladders) — the pricing engine
/// only cares about the <b>order</b>, so a boost from A to B costs the sum of the
/// divisions between them.
/// </summary>
public sealed partial class RankLadder
{
    /// <summary>Divisions from lowest to highest. Index = position on the ladder.</summary>
    public List<string> Divisions { get; set; } = [.. DefaultValorant()];

    /// <summary>Valorant's real ladder: Iron 1 … Immortal 3, then Radiant (25 steps).</summary>
    public static IReadOnlyList<string> DefaultValorant()
    {
        var list = new List<string>();
        foreach (var tier in ValorantRanks.Tiers)
        {
            if (tier == "Radiant")
            {
                list.Add(tier);
                continue;
            }

            for (var division = 1; division <= 3; division++)
            {
                list.Add($"{tier} {division}");
            }
        }

        return list;
    }

    public int Count => Divisions.Count;

    public string this[int index] => Divisions[index];

    /// <summary>
    /// Position of <paramref name="rank"/> on the ladder, or -1 when unknown.
    /// Matching is forgiving: case, extra spaces, roman numerals and short tier
    /// names ("plat 2", "PLATINUM II", "Platinum2") all resolve to the same entry.
    /// </summary>
    public int IndexOf(string? rank)
    {
        if (string.IsNullOrWhiteSpace(rank))
        {
            return -1;
        }

        var needle = Normalize(rank);
        for (var i = 0; i < Divisions.Count; i++)
        {
            if (Normalize(Divisions[i]) == needle)
            {
                return i;
            }
        }

        // Fall back to the first division of the same tier ("Gold" → "Gold 1").
        var tier = Normalize(ValorantRanks.Tier(rank));
        for (var i = 0; i < Divisions.Count; i++)
        {
            if (Normalize(ValorantRanks.Tier(Divisions[i])) == tier)
            {
                return i;
            }
        }

        return -1;
    }

    public bool Contains(string? rank) => IndexOf(rank) >= 0;

    /// <summary>The canonical ladder entry for a loosely written rank, or null.</summary>
    public string? Canonical(string? rank)
    {
        var index = IndexOf(rank);
        return index < 0 ? null : Divisions[index];
    }

    /// <summary>
    /// Divisions billed for a climb, i.e. every step the booster has to win into.
    /// With <paramref name="includeStart"/> the starting division is billed too
    /// (the "you also pay for finishing the rank you're in" reading).
    /// Returns an empty list when the range is unknown or not a climb.
    /// </summary>
    public IReadOnlyList<string> Steps(string? from, string? to, bool includeStart)
    {
        var start = IndexOf(from);
        var end = IndexOf(to);
        if (start < 0 || end < 0 || end <= start)
        {
            return [];
        }

        var first = includeStart ? start : start + 1;
        return Divisions.GetRange(first, end - first + 1);
    }

    [GeneratedRegex(@"[\s_\-]+")]
    private static partial Regex Whitespace();

    /// <summary>Lower-cases, collapses separators and rewrites roman numerals so "Plat II" == "platinum 2".</summary>
    private static string Normalize(string value)
    {
        var text = Whitespace().Replace(value.Trim().ToLowerInvariant(), " ");
        text = text.Replace(" iii", " 3").Replace(" ii", " 2").Replace(" i", " 1");

        // Expand short tier names so "plat 2" matches "platinum 2".
        foreach (var (shortName, full) in Aliases)
        {
            if (text.StartsWith(shortName, StringComparison.Ordinal) &&
                (text.Length == shortName.Length || text[shortName.Length] == ' '))
            {
                text = string.Concat(full, text.AsSpan(shortName.Length));
                break;
            }
        }

        return text.Replace(" ", "");
    }

    private static readonly (string Short, string Full)[] Aliases =
    [
        ("plat", "platinum"),
        ("dia", "diamond"),
        ("dmd", "diamond"),
        ("asc", "ascendant"),
        ("ascen", "ascendant"),
        ("immo", "immortal"),
        ("imm", "immortal"),
        ("rad", "radiant"),
    ];
}
