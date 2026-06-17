using System.Windows.Media;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>Display row for one incoming boosting request: parsed range/region + computed offer.</summary>
public sealed class RequestRow
{
    public string Buyer { get; }
    public string CategoryTitle { get; }
    public string CurrentRank { get; }
    public string DesiredRank { get; }
    public string Region { get; }
    public string Created { get; }
    public string PriceText { get; }
    public string TimeText { get; }
    public string Status { get; }
    public Brush StatusBrush { get; }
    public Brush CurrentRankBrush { get; }
    public Brush DesiredRankBrush { get; }

    private static readonly Brush Green = Hex("#69F0AE");
    private static readonly Brush Amber = Hex("#FFB300");
    private static readonly Brush Grey = Hex("#9E9E9E");

    public RequestRow(BoostingRequest request, BoostingBotSettings settings)
    {
        Buyer = request.BuyerUsername ?? "—";
        CategoryTitle = request.BoostingCategoryTitle ?? "(senza titolo)";
        Created = request.CreatedDate.ToLocalTime().ToString("dd/MM HH:mm");

        var parsed = BoostingCategoryParser.Parse(request.BoostingCategoryTitle);
        CurrentRank = parsed.CurrentRank ?? "?";
        DesiredRank = parsed.DesiredRank ?? "?";
        Region = parsed.Region ?? "?";
        CurrentRankBrush = Hex(ValorantRanks.ColorHex(CurrentRank));
        DesiredRankBrush = Hex(ValorantRanks.ColorHex(DesiredRank));

        var currentTier = parsed.CurrentRank is null ? null : ValorantRanks.Tier(parsed.CurrentRank);

        if (!settings.IsRegionAccepted(parsed.Region))
        {
            Status = $"Regione esclusa ({Region})";
            StatusBrush = Grey;
            PriceText = "—";
            TimeText = "";
        }
        else if (settings.IsRankExcluded(parsed.DesiredTier) || settings.IsRankExcluded(currentTier))
        {
            Status = "Rank escluso";
            StatusBrush = Grey;
            PriceText = "—";
            TimeText = "";
        }
        else
        {
            var quote = BoostOfferCalculator.Quote(settings, parsed.CurrentRank, parsed.DesiredRank);
            if (quote is null)
            {
                Status = "Range non interpretabile";
                StatusBrush = Amber;
                PriceText = "—";
                TimeText = "";
            }
            else
            {
                Status = "Offrirei";
                StatusBrush = Green;
                PriceText = $"{quote.Price:N2} {settings.Currency}";
                TimeText = $"~{quote.Hours:0.#}h → {quote.DeliveryTime}";
            }
        }
    }

    private static Brush Hex(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
