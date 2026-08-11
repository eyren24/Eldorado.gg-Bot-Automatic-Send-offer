using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EldoradoApp.Services;

/// <summary>
/// Fallback channel: puts the composed message on the clipboard — together with the
/// banner image when one is configured — so the seller only has to press Ctrl+V in the
/// buyer conversation. Always available, which is why it's the last resort of
/// <see cref="OfferMessageDispatcher"/>.
/// </summary>
public sealed class ClipboardOfferMessenger(Dispatcher dispatcher) : IOfferMessenger
{
    public bool IsReady => true;

    public string Name => "Appunti";

    public Task<OfferMessageResult> SendAsync(
        OutgoingOfferMessage message, CancellationToken cancellationToken = default)
    {
        var result = dispatcher.Invoke(() => Stage(message));
        return Task.FromResult(result);
    }

    private static OfferMessageResult Stage(OutgoingOfferMessage message)
    {
        try
        {
            var data = new DataObject();
            data.SetText(message.Text);

            var withBanner = false;
            if (message.HasBanner && File.Exists(message.BannerPath!))
            {
                // Both forms: the image for chats that accept a pasted bitmap, and the
                // file drop for those that expect a real attachment.
                data.SetImage(LoadBitmap(message.BannerPath!));
                data.SetFileDropList(new StringCollection { message.BannerPath! });
                withBanner = true;
            }

            Clipboard.SetDataObject(data, copy: true);

            return OfferMessageResult.Staged(withBanner
                ? "messaggio + banner copiati negli appunti (incolla in chat con Ctrl+V)"
                : "messaggio copiato negli appunti (incolla in chat con Ctrl+V)");
        }
        catch (Exception ex)
        {
            return OfferMessageResult.Failed($"copia negli appunti fallita: {ex.Message}");
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;   // release the file handle immediately
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
