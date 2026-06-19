using System.Windows.Media;
using EldoradoApp.Models;

namespace EldoradoApp.ViewModels;

/// <summary>Display row for one incoming boosting request: its category + the offer the bot would make.</summary>
public sealed class RequestRow
{
    public string Buyer { get; }
    public string CategoryTitle { get; }
    public string Created { get; }
    public string PriceText { get; }
    public string TimeText { get; }
    public string Status { get; }
    public Brush StatusBrush { get; }

    private static readonly Brush Green = Hex("#69F0AE");
    private static readonly Brush Amber = Hex("#FFB300");
    private static readonly Brush Grey = Hex("#9E9E9E");

    public RequestRow(BoostingRequest request, BoostingBotSettings settings)
    {
        Buyer = request.BuyerUsername ?? "—";
        CategoryTitle = request.BoostingCategoryTitle ?? "(senza categoria)";
        Created = request.CreatedDate.ToLocalTime().ToString("dd/MM HH:mm");

        var pricing = settings.ForCategory(request.GameId, request.BoostingCategoryId);

        if (pricing is null)
        {
            Status = "Categoria non configurata";
            StatusBrush = Grey;
            PriceText = "—";
            TimeText = "";
        }
        else if (!pricing.Enabled)
        {
            Status = "Categoria disattivata";
            StatusBrush = Grey;
            PriceText = "—";
            TimeText = "";
        }
        else if (pricing.PricePerUnit <= 0)
        {
            Status = "Prezzo non impostato";
            StatusBrush = Amber;
            PriceText = "—";
            TimeText = "";
        }
        else
        {
            Status = "Offrirei";
            StatusBrush = Green;
            PriceText = $"{pricing.PricePerUnit:N2} {settings.Currency}/unità";
            TimeText = $"qta {pricing.MinQuantity}-{pricing.Quantity} → {pricing.DeliveryTime}";
        }
    }

    private static Brush Hex(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
