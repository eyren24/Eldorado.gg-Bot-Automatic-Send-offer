using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>What happened to one post-offer message; the UI keeps these as a history list.</summary>
public sealed record OfferMessageRecord(
    OutgoingOfferMessage Message,
    OfferMessageResult Result,
    string Channel);

/// <summary>
/// Fires the seller's message (and banner) the moment an offer goes out.
/// <para>
/// It composes the text, waits the configured grace period so the buyer conversation
/// exists, then tries the automatic channel — <see cref="Primary"/>, the app's own
/// logged-in browser — retrying up to <see cref="OfferMessageSettings.MaxAttempts"/>,
/// and finally falls back to staging everything on the clipboard so nothing is lost.
/// </para>
/// </summary>
public sealed class OfferMessageDispatcher(
    Func<BoostingBotSettings> settingsProvider,
    IOfferMessenger fallback)
{
    /// <summary>The automatic channel. Wired by the shell once its browser is alive.</summary>
    public IOfferMessenger? Primary { get; set; }

    /// <summary>Raised after every delivery attempt, successful or not.</summary>
    public event Action<OfferMessageRecord>? Delivered;

    public async Task<OfferMessageResult> DispatchAsync(
        BoostingRequest request,
        PriceQuote quote,
        BoostingDeliveryTime deliveryTime,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsProvider();
        var config = settings.Message;

        if (!config.Enabled)
        {
            return new OfferMessageResult(MessageOutcome.Disabled, "messaggio automatico disattivato");
        }

        var text = OfferMessageComposer.Compose(config.Template, request, quote, deliveryTime);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new OfferMessageResult(MessageOutcome.Disabled, "template del messaggio vuoto");
        }

        var message = new OutgoingOfferMessage(
            RequestId: request.Id,
            BuyerId: request.BuyerId,
            BuyerUsername: request.BuyerUsername,
            Text: text,
            BannerPath: config.HasBanner ? config.BannerPath : null,
            CreatedAt: DateTimeOffset.Now);

        if (config.DelaySeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(config.DelaySeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return OfferMessageResult.Failed("invio annullato");
            }
        }

        var result = await TryPrimaryAsync(message, config, cancellationToken).ConfigureAwait(false);
        var channel = Primary?.Name ?? "—";

        if (result is null || result.Outcome != MessageOutcome.Sent)
        {
            // Nothing automatic worked (or it's disabled): make sure the seller can paste it.
            // When no automatic channel exists at all — e.g. a PC without WebView2 — always
            // stage it, otherwise the message would just vanish.
            var noAutomaticChannel = Primary is not { IsReady: true };

            if (config.CopyToClipboard || config.Delivery == MessageDelivery.ClipboardOnly || noAutomaticChannel)
            {
                var staged = await fallback.SendAsync(message, cancellationToken).ConfigureAwait(false);
                var reason = result is null ? "" : $" · {result.Detail}";
                result = staged with { Detail = staged.Detail + reason };
                channel = fallback.Name;
            }
            else
            {
                result ??= OfferMessageResult.Failed("nessun canale di invio disponibile");
            }
        }

        Delivered?.Invoke(new OfferMessageRecord(message, result, channel));
        return result;
    }

    /// <summary>Runs the automatic channel with retries; null when it isn't usable at all.</summary>
    private async Task<OfferMessageResult?> TryPrimaryAsync(
        OutgoingOfferMessage message, OfferMessageSettings config, CancellationToken cancellationToken)
    {
        if (config.Delivery != MessageDelivery.AutoBrowser)
        {
            return null;
        }

        if (Primary is not { IsReady: true })
        {
            return OfferMessageResult.Failed("browser integrato non pronto (apri la scheda Chat e accedi)");
        }

        var attempts = Math.Max(1, config.MaxAttempts);
        OfferMessageResult last = OfferMessageResult.Failed("nessun tentativo eseguito");

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                last = await Primary.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return OfferMessageResult.Failed("invio annullato");
            }
            catch (Exception ex)
            {
                last = OfferMessageResult.Failed($"tentativo {attempt}: {ex.Message}");
            }

            if (last.Outcome == MessageOutcome.Sent)
            {
                return last;
            }

            if (attempt < attempts)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return OfferMessageResult.Failed("invio annullato");
                }
            }
        }

        return last;
    }
}
