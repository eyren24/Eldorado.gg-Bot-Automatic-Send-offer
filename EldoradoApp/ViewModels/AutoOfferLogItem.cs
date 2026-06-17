using System.Windows.Media;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>One line in the bot's live activity log.</summary>
public sealed class AutoOfferLogItem
{
    public string Time { get; }
    public string Message { get; }
    public Brush Color { get; }

    public AutoOfferLogItem(AutoOfferEvent e)
    {
        Time = e.Timestamp.ToString("HH:mm:ss");
        var category = string.IsNullOrWhiteSpace(e.CategoryTitle) ? "" : $" · {e.CategoryTitle}";
        Message = $"{e.Message}{category}";
        Color = new SolidColorBrush(e.Outcome switch
        {
            AutoOfferOutcome.Submitted => Colors.LightGreen,
            AutoOfferOutcome.Accepted => System.Windows.Media.Color.FromRgb(0xFF, 0xD5, 0x4F),
            AutoOfferOutcome.DryRunWouldSubmit => System.Windows.Media.Color.FromRgb(0x4F, 0xC3, 0xF7),
            AutoOfferOutcome.Error => System.Windows.Media.Color.FromRgb(0xFF, 0x52, 0x52),
            _ => System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)
        });
    }
}
