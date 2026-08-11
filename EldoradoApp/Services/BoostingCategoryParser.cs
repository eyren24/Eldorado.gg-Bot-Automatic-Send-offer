using System.Text.RegularExpressions;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>Rank range, region, game count and buyer options extracted from a boosting request.</summary>
public sealed record ParsedBoostingCategory(
    string RawTitle,
    string? Region,
    string? CurrentRank,
    string? DesiredRank,
    IReadOnlyList<string> MatchedExtraIds,
    int? Quantity = null)
{
    /// <summary>The tier of the destination rank (used for exclusion checks), or null.</summary>
    public string? DesiredTier => DesiredRank is null ? null : ValorantRanks.Tier(DesiredRank);

    /// <summary>
    /// The tier the per-game price is read from: last season's rank for placements,
    /// the current one for net wins — both are the first rank mentioned.
    /// </summary>
    public string? UnitTier => CurrentRank is null ? null : ValorantRanks.Tier(CurrentRank);

    public bool HasRange => CurrentRank is not null && DesiredRank is not null;
}

/// <summary>
/// Best-effort parser that pulls a rank range, a region and the buyer's requested
/// options out of a request's free-text title (falling back to the raw payload).
/// Rank tokens are resolved against the seller's own <see cref="RankLadder"/>, so a
/// customised ladder keeps working.
/// </summary>
public static partial class BoostingCategoryParser
{
    // Longest tokens first so "platinum" wins over "plat", "immortal" over "imm", etc.
    [GeneratedRegex(
        @"\b(platinum|plat|diamond|dmd|dia|ascendant|ascen|asc|immortal|immo|imm|radiant|rad|iron|bronze|silver|gold)\s*([1-5]|i{1,3})?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RankRegex();

    [GeneratedRegex(
        @"\b(euw|eu|europe|north\s*america|na|apac|asia|ap|korea|kr|latam|brazil|br|oceania|oce|turkey|tr|mena)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RegionRegex();

    /// <summary>"10 wins", "5 placement matches", "x10", "10x" — how many games are wanted.</summary>
    [GeneratedRegex(
        @"(?:\bx\s*(?<a>\d{1,3})\b)|(?:\b(?<b>\d{1,3})\s*(?:x\b|games?|matches?|partite|wins?|win\b|vittorie|placements?))",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuantityRegex();

    /// <summary>Parses a request against the seller's settings (ladder + extras).</summary>
    public static ParsedBoostingCategory Parse(BoostingRequest request, BoostingBotSettings settings)
    {
        var title = request.BoostingCategoryTitle ?? "";
        var (current, desired) = ReadRange(title, settings.Pricing.Ladder);

        // The title is the reliable source; only dig into the raw payload if it had nothing.
        if (current is null && !string.IsNullOrWhiteSpace(request.RawJson))
        {
            (current, desired) = ReadRange(request.RawJson, settings.Pricing.Ladder);
        }

        var haystack = request.SearchText;
        var region = FindRegion(haystack);
        var extras = settings.Extras
            .Where(e => e.Enabled && e.Matches(haystack))
            .Select(e => e.Id)
            .ToList();

        return new ParsedBoostingCategory(title, region, current, desired, extras, FindQuantity(title));
    }

    /// <summary>Title-only overload (used by the price simulator and by tests).</summary>
    public static ParsedBoostingCategory Parse(string? title, RankLadder? ladder = null)
    {
        var raw = title ?? "";
        var (current, desired) = ReadRange(raw, ladder ?? new RankLadder());
        return new ParsedBoostingCategory(raw, FindRegion(raw), current, desired, [], FindQuantity(raw));
    }

    /// <summary>How many games/wins the request asks for, or null when it doesn't say.</summary>
    public static int? FindQuantity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in QuantityRegex().Matches(text))
        {
            var raw = match.Groups["a"].Success ? match.Groups["a"].Value : match.Groups["b"].Value;
            if (int.TryParse(raw, out var value) && value is > 0 and <= 200)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>First and last rank mentioned, canonicalised onto the ladder.</summary>
    private static (string? Current, string? Desired) ReadRange(string text, RankLadder ladder)
    {
        var ranks = new List<string>();

        foreach (Match m in RankRegex().Matches(text))
        {
            var tier = CanonicalTier(m.Groups[1].Value);
            if (tier is null)
            {
                continue;
            }

            var division = CanonicalDivision(m.Groups[2].Value);
            var token = division is null ? tier : $"{tier} {division}";

            // Only keep ranks the seller's ladder actually knows about.
            var canonical = ladder.Canonical(token);
            if (canonical is not null)
            {
                ranks.Add(canonical);
            }
        }

        return ranks.Count switch
        {
            0 => (null, null),
            1 => (ranks[0], null),
            _ => (ranks[0], ranks[^1])
        };
    }

    private static string? FindRegion(string text)
    {
        var match = RegionRegex().Match(text);
        return match.Success ? CanonicalRegion(match.Value) : null;
    }

    private static string? CanonicalDivision(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return token.ToLowerInvariant() switch
        {
            "i" => "1",
            "ii" => "2",
            "iii" => "3",
            _ => token
        };
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
