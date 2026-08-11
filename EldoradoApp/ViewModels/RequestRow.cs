using System.Windows.Media;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>
/// One incoming boosting request in the feed: what the buyer asked for, the range the
/// parser read out of it, and the exact price the bot would offer — with the full
/// breakdown, so a number is never a mystery.
/// </summary>
public sealed class RequestRow
{
    public string Buyer { get; }
    public string CategoryTitle { get; }
    public string Created { get; }
    public string RangeText { get; }
    public string PriceText { get; }
    public string DetailText { get; }
    public string ExtrasText { get; }
    public string Status { get; }
    public Brush StatusBrush { get; }
    public Brush RankBrush { get; }
    public string RawJson { get; }
    public IReadOnlyList<PriceLine> Lines { get; }

    private static readonly Brush Green = Frozen("#69F0AE");
    private static readonly Brush Amber = Frozen("#FFB300");
    private static readonly Brush Grey = Frozen("#9E9E9E");

    public RequestRow(BoostingRequest request, BoostingBotSettings settings)
    {
        Buyer = request.BuyerUsername ?? "—";
        CategoryTitle = request.BoostingCategoryTitle ?? "(senza categoria)";
        Created = request.CreatedDate.ToLocalTime().ToString("dd/MM HH:mm");
        RawJson = request.RawJson ?? "";

        var parsed = BoostingCategoryParser.Parse(request, settings);
        var quote = BoostingPriceCalculator.Quote(request, settings);
        var category = settings.ForCategory(request.GameId, request.BoostingCategoryId);

        Lines = quote.Lines;
        RangeText = quote.RangeText;
        RankBrush = Frozen(ValorantRanks.ColorHex(quote.ToRank ?? quote.FromRank ?? "Iron"));

        var extras = settings.Extras
            .Where(e => parsed.MatchedExtraIds.Contains(e.Id))
            .Select(e => e.Label)
            .ToList();
        ExtrasText = extras.Count == 0 ? "" : string.Join(" · ", extras);

        if (category is { Enabled: false })
        {
            Status = "Categoria disattivata";
            StatusBrush = Grey;
            PriceText = "—";
            DetailText = CategoryTitle;
        }
        else if (category is null && !settings.AnswerUnknownCategories)
        {
            Status = "Categoria non configurata";
            StatusBrush = Grey;
            PriceText = "—";
            DetailText = CategoryTitle;
        }
        else if (!settings.IsRegionAccepted(parsed.Region))
        {
            Status = $"Regione {parsed.Region} esclusa";
            StatusBrush = Grey;
            PriceText = "—";
            DetailText = CategoryTitle;
        }
        else if (!quote.IsPriceable)
        {
            Status = quote.Problem ?? "Non quotabile";
            StatusBrush = Amber;
            PriceText = "—";
            DetailText = CategoryTitle;
        }
        else
        {
            Status = settings.DryRun ? "Offrirei (dry-run)" : "Offerta automatica";
            StatusBrush = Green;
            PriceText = quote.TotalText;

            var delivery = settings.DeliveryTimeFor(request.GameId, request.BoostingCategoryId);
            DetailText = $"{quote.DivisionCount} divisioni · consegna {OfferMessageComposer.DeliveryText(delivery)}";
        }
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
