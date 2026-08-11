using System.Windows.Media;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>One line in the bot's live activity log.</summary>
public sealed class AutoOfferLogItem
{
    public string Time { get; }
    public string Message { get; }
    public string Buyer { get; }
    public Brush Foreground { get; }

    public AutoOfferLogItem(AutoOfferEvent e)
    {
        Time = e.Timestamp.ToString("HH:mm:ss");
        Buyer = e.BuyerUsername ?? "";

        var category = string.IsNullOrWhiteSpace(e.CategoryTitle) ? "" : $" · {e.CategoryTitle}";
        Message = $"{e.Message}{category}";

        Foreground = new SolidColorBrush(e.Outcome switch
        {
            AutoOfferOutcome.Submitted => Colors.LightGreen,
            AutoOfferOutcome.Accepted => Color.FromRgb(0xFF, 0xD5, 0x4F),
            AutoOfferOutcome.DryRunWouldSubmit => Color.FromRgb(0x4F, 0xC3, 0xF7),
            AutoOfferOutcome.Message => Color.FromRgb(0xB3, 0x9D, 0xDB),
            AutoOfferOutcome.Error => Color.FromRgb(0xFF, 0x52, 0x52),
            AutoOfferOutcome.SkippedNoRange => Color.FromRgb(0xFF, 0xB3, 0x00),
            _ => Color.FromRgb(0x9E, 0x9E, 0x9E)
        });
        Foreground.Freeze();
    }
}
