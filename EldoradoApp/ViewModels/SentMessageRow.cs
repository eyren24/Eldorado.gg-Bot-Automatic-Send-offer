using System.Windows.Media;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>One entry of the "messaggi inviati" history.</summary>
public sealed class SentMessageRow
{
    public string Time { get; }
    public string Buyer { get; }
    public string Channel { get; }
    public string Status { get; }
    public string Text { get; }
    public string Banner { get; }
    public Brush StatusBrush { get; }

    public SentMessageRow(OfferMessageRecord record)
    {
        Time = record.Message.CreatedAt.ToString("HH:mm:ss");
        Buyer = record.Message.BuyerUsername ?? "—";
        Channel = record.Channel;
        Status = record.Result.Detail;
        Text = record.Message.Text;
        Banner = record.Message.HasBanner ? "🖼️ banner allegato" : "";
        StatusBrush = new SolidColorBrush(record.Result.Outcome switch
        {
            MessageOutcome.Sent => Color.FromRgb(0x69, 0xF0, 0xAE),
            MessageOutcome.Staged => Color.FromRgb(0x4F, 0xC3, 0xF7),
            MessageOutcome.Failed => Color.FromRgb(0xFF, 0x52, 0x52),
            _ => Color.FromRgb(0x9E, 0x9E, 0x9E)
        });
        StatusBrush.Freeze();
    }
}
