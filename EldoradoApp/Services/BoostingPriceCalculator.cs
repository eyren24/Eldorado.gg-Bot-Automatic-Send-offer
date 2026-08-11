using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Turns a boosting request into the price to offer. Three shapes of job, one tail:
/// <code>
/// rank boost  : base + Σ prezzo(divisione scalata)
/// placement   : base + prezzoPerPartita(tier stagione scorsa) × partite
/// net wins    : base + prezzoPerVittoria(tier attuale) × vittorie
///             → × moltiplicatore globale → × moltiplicatore regione
///             + extra fissi e percentuali → minimo, arrotondamento
/// </code>
/// Every term lands in <see cref="PriceQuote.Lines"/>, so the number is always explainable.
/// </summary>
public static class BoostingPriceCalculator
{
    /// <summary>Prices a live request: kind, rank, game count and extras all come from it.</summary>
    public static PriceQuote Quote(BoostingRequest request, BoostingBotSettings settings)
    {
        var parsed = BoostingCategoryParser.Parse(request, settings);
        var category = settings.ForCategory(request.GameId, request.BoostingCategoryId);

        // A category with an explicit flat price ignores every other rule.
        if (category is { FlatPrice: > 0 })
        {
            var flat = new List<PriceLine>
            {
                new($"Prezzo fisso · {category.CategoryName}", category.FlatPrice, PriceLineKind.Base)
            };

            return Finish(settings, parsed, flat, category.FlatPrice, BoostingCategoryKind.Flat, 0, []);
        }

        var kind = settings.KindFor(request.GameId, request.BoostingCategoryId, request.BoostingCategoryTitle);

        return kind switch
        {
            BoostingCategoryKind.Placements => QuoteUnits(settings, parsed, BoostingCategoryKind.Placements),
            BoostingCategoryKind.NetWins => QuoteUnits(settings, parsed, BoostingCategoryKind.NetWins),
            _ => QuoteLadder(settings, parsed)
        };
    }

    // ---- Rank boost (division ladder) ----

    /// <summary>Prices an explicit range — used by the request feed and the price simulator.</summary>
    public static PriceQuote Quote(
        string? from,
        string? to,
        IReadOnlyList<string> extraIds,
        BoostingBotSettings settings,
        string? region = null) =>
        QuoteLadder(settings, new ParsedBoostingCategory("", region, from, to, extraIds));

    private static PriceQuote QuoteLadder(BoostingBotSettings settings, ParsedBoostingCategory parsed)
    {
        var pricing = settings.Pricing;

        if (parsed.CurrentRank is null || parsed.DesiredRank is null)
        {
            return PriceQuote.Unpriceable(
                settings.Currency, parsed.CurrentRank, parsed.DesiredRank, "range di rank non riconosciuto");
        }

        var steps = pricing.Ladder.Steps(parsed.CurrentRank, parsed.DesiredRank, pricing.IncludeStartDivision);
        if (steps.Count == 0)
        {
            return PriceQuote.Unpriceable(
                settings.Currency, parsed.CurrentRank, parsed.DesiredRank,
                "il rank finale non è più alto di quello iniziale");
        }

        var lines = new List<PriceLine> { new("Prezzo base", pricing.BasePrice, PriceLineKind.Base) };
        lines.AddRange(steps.Select(d => new PriceLine(d, pricing.PriceOf(d), PriceLineKind.Division)));

        return Finish(settings, parsed, lines, lines.Sum(l => l.Amount),
            BoostingCategoryKind.RankBoost, 0, steps);
    }

    // ---- Placements / net wins (price per game) ----

    /// <summary>Prices a per-game category from an explicit tier + count (used by the simulator).</summary>
    public static PriceQuote QuoteUnits(
        BoostingCategoryKind kind,
        string? tier,
        int? quantity,
        IReadOnlyList<string> extraIds,
        BoostingBotSettings settings,
        string? region = null) =>
        QuoteUnits(settings, new ParsedBoostingCategory("", region, tier, null, extraIds, quantity), kind);

    private static PriceQuote QuoteUnits(
        BoostingBotSettings settings, ParsedBoostingCategory parsed, BoostingCategoryKind kind)
    {
        var pricing = kind == BoostingCategoryKind.Placements ? settings.Placements : settings.NetWins;
        var label = kind == BoostingCategoryKind.Placements ? "partite di placement" : "net wins";

        if (!pricing.Enabled)
        {
            return PriceQuote.Unpriceable(settings.Currency, parsed.CurrentRank, null,
                $"categoria «{label}» disattivata nelle impostazioni", kind);
        }

        var tier = parsed.UnitTier;
        if (tier is null)
        {
            return PriceQuote.Unpriceable(settings.Currency, null, null,
                "rank non riconosciuto nella richiesta", kind);
        }

        var perUnit = pricing.PriceOf(tier);
        if (perUnit <= 0)
        {
            return PriceQuote.Unpriceable(settings.Currency, parsed.CurrentRank, null,
                $"prezzo per partita non impostato per {tier}", kind);
        }

        var units = pricing.Clamp(parsed.Quantity);
        var lines = new List<PriceLine>
        {
            new("Prezzo base", pricing.BasePrice, PriceLineKind.Base),
            new($"{tier} · {units} × {perUnit:N2}", perUnit * units, PriceLineKind.Unit)
        };

        return Finish(settings, parsed, lines, lines.Sum(l => l.Amount), kind, units, []);
    }

    // ---- Shared tail: multipliers, extras, floor, rounding ----

    private static PriceQuote Finish(
        BoostingBotSettings settings,
        ParsedBoostingCategory parsed,
        List<PriceLine> lines,
        decimal subtotal,
        BoostingCategoryKind kind,
        int units,
        IReadOnlyList<string> billedDivisions)
    {
        var pricing = settings.Pricing;

        if (pricing.Multiplier != 1m && pricing.Multiplier > 0)
        {
            var delta = subtotal * (pricing.Multiplier - 1m);
            lines.Add(new PriceLine($"Moltiplicatore ×{pricing.Multiplier:0.##}", delta, PriceLineKind.Adjustment));
            subtotal += delta;
        }

        // Out-of-region jobs (NA, APAC, …) carry their own factor.
        var regionFactor = settings.RegionMultiplier(parsed.Region);
        if (regionFactor != 1m)
        {
            var delta = subtotal * (regionFactor - 1m);
            lines.Add(new PriceLine($"Regione {parsed.Region} ×{regionFactor:0.##}", delta, PriceLineKind.Adjustment));
            subtotal += delta;
        }

        foreach (var extra in settings.Extras.Where(e => e.Enabled && parsed.MatchedExtraIds.Contains(e.Id)))
        {
            var amount = extra.Kind == ExtraKind.Fixed
                ? extra.Amount
                : subtotal * extra.Amount / 100m;

            var label = extra.Kind == ExtraKind.Fixed
                ? $"Extra · {extra.Label}"
                : $"Extra · {extra.Label} (+{extra.Amount:0.##}%)";

            lines.Add(new PriceLine(label, amount, PriceLineKind.Extra));
        }

        var total = lines.Sum(l => l.Amount);

        if (pricing.MinimumPrice > 0 && total < pricing.MinimumPrice)
        {
            lines.Add(new PriceLine("Minimo applicato", pricing.MinimumPrice - total, PriceLineKind.Adjustment));
            total = pricing.MinimumPrice;
        }

        if (pricing.RoundTo > 0)
        {
            var rounded = Math.Ceiling(total / pricing.RoundTo) * pricing.RoundTo;
            if (rounded != total)
            {
                lines.Add(new PriceLine($"Arrotondamento a {pricing.RoundTo:0.##}", rounded - total, PriceLineKind.Adjustment));
                total = rounded;
            }
        }

        total = Math.Round(total, 2, MidpointRounding.AwayFromZero);

        return new PriceQuote(
            total,
            settings.Currency,
            parsed.CurrentRank,
            parsed.DesiredRank,
            billedDivisions,
            lines,
            Problem: total > 0 ? null : "totale a zero: controlla prezzo base e listino",
            Kind: kind,
            Units: units,
            Region: parsed.Region);
    }

    /// <summary>
    /// Base price + extras only, for sellers who prefer bidding on requests whose rank
    /// range couldn't be read rather than skipping them.
    /// </summary>
    public static PriceQuote QuoteWithoutRange(
        IReadOnlyList<string> extraIds, BoostingBotSettings settings, string? region = null)
    {
        var lines = new List<PriceLine> { new("Prezzo base", settings.Pricing.BasePrice, PriceLineKind.Base) };
        var parsed = new ParsedBoostingCategory("", region, null, null, extraIds);
        return Finish(settings, parsed, lines, settings.Pricing.BasePrice,
            BoostingCategoryKind.RankBoost, 0, []);
    }

    /// <summary>
    /// Splits a total into Eldorado's per-unit offer pricing
    /// (<c>pricePerUnit × quantity</c>), following <see cref="OfferPriceMode"/>.
    /// </summary>
    public static (decimal PricePerUnit, int Quantity, int MinQuantity) ToOfferPricing(
        PriceQuote quote, BoostingBotSettings settings, CategoryPricing? category)
    {
        if (settings.Pricing.PriceMode == OfferPriceMode.PerDivision && quote.DivisionCount > 0)
        {
            var perDivision = Math.Round(quote.Total / quote.DivisionCount, 2, MidpointRounding.AwayFromZero);
            return (perDivision, quote.DivisionCount, quote.DivisionCount);
        }

        var quantity = Math.Max(1, category?.Quantity ?? 1);
        var minQuantity = Math.Clamp(category?.MinQuantity ?? 1, 1, quantity);
        return (quote.Total, quantity, minQuantity);
    }
}
