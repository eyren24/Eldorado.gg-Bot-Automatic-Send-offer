using System.Text.RegularExpressions;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>Rank range + region extracted from a boosting category title.</summary>
public sealed record ParsedBoostingCategory(string RawTitle, string? Region, string? CurrentRank, string? DesiredRank)
{
    /// <summary>The tier of the destination rank (used for exclusion checks), or null.</summary>
    public string? DesiredTier => DesiredRank is null ? null : ValorantRanks.Tier(DesiredRank);

    public bool HasRange => CurrentRank is not null && DesiredRank is not null;
}

/// <summary>
/// Best-effort parser that pulls a Valorant rank range and region out of a free-text
/// category/request title. The exact title format on Eldorado is unknown until live,
/// so the engine logs raw titles in dry-run to calibrate these patterns.
/// </summary>
public static partial class BoostingCategoryParser
{
    // Longest tokens first so "platinum" wins over "plat", "immortal" over "imm", etc.
    [GeneratedRegex(
        @"\b(platinum|plat|diamond|dmd|dia|ascendant|ascen|asc|immortal|immo|imm|radiant|rad|iron|bronze|silver|gold)\s*([1-3])?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RankRegex();

    [GeneratedRegex(
        @"\b(euw|eu|europe|north\s*america|na|apac|asia|ap|korea|kr|latam|brazil|br|oceania|oce|turkey|tr|mena)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RegionRegex();

    public static ParsedBoostingCategory Parse(string? title)
    {
        var raw = title ?? "";
        var ranks = new List<string>();

        foreach (Match m in RankRegex().Matches(raw))
        {
            var tier = CanonicalTier(m.Groups[1].Value);
            if (tier is null)
            {
                continue;
            }

            var div = m.Groups[2].Value;
            ranks.Add(tier == "Radiant" || string.IsNullOrEmpty(div) ? tier : $"{tier} {div}");
        }

        string? current = ranks.Count > 0 ? ranks[0] : null;
        string? desired = ranks.Count > 1 ? ranks[^1] : null;

        var regionMatch = RegionRegex().Match(raw);
        var region = regionMatch.Success ? CanonicalRegion(regionMatch.Value) : null;

        return new ParsedBoostingCategory(raw, region, current, desired);
    }

    private static string? CanonicalTier(string token) => token.ToLowerInvariant() switch
    {
        "iron" => "Iron",
        "bronze" => "Bronze",
        "silver" => "Silver",
        "gold" => "Gold",
        "platinum" or "plat" => "Platinum",
        "diamond" or "dia" or "dmd" => "Diamond",
        "ascendant" or "ascen" or "asc" => "Ascendant",
        "immortal" or "immo" or "imm" => "Immortal",
        "radiant" or "rad" => "Radiant",
        _ => null
    };

    private static string CanonicalRegion(string token) => Regex.Replace(token, @"\s+", " ").ToLowerInvariant() switch
    {
        "eu" or "euw" or "europe" => "EU",
        "na" or "north america" => "NA",
        "ap" or "apac" or "asia" => "AP",
        "kr" or "korea" => "KR",
        "latam" => "LATAM",
        "br" or "brazil" => "BR",
        "oce" or "oceania" => "OCE",
        "tr" or "turkey" => "TR",
        "mena" => "MENA",
        _ => token.ToUpperInvariant()
    };
}
