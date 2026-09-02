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
/// exists, then reserves a durable outbox entry before using the selected automatic
/// channel. A timeout after a click is <i>unknown</i>, not a reason to send a duplicate.
/// </para>
/// </summary>
public sealed class OfferMessageDispatcher(
    Func<BoostingBotSettings> settingsProvider,
    IOfferMessenger fallback)
{
    private readonly OfferMessageOutbox _outbox = new();

    /// <summary>Legacy WebView2 sender, still available for an explicit compatibility choice.</summary>
    public IOfferMessenger? BrowserPrimary { get; set; }

    /// <summary>The dedicated, persistent Playwright sender used by default for new settings.</summary>
    public IOfferMessenger? PlaywrightPrimary { get; set; }

    /// <summary>Compatibility alias used by the existing Chat page when it creates WebView2.</summary>
    public IOfferMessenger? Primary
    {
        get => BrowserPrimary;
        set => BrowserPrimary = value;
    }

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

        if (!RemoteControlGate.AllowsMessaging())
        {
            return OfferMessageResult.Failed("messaggistica disattivata dal controllo server");
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

        var reservation = _outbox.Reserve(message);
        if (reservation.Decision == OutboxDecision.AlreadySent)
        {
            var duplicate = OfferMessageResult.Sent("messaggio già registrato come inviato: nessun doppione");
            Delivered?.Invoke(new OfferMessageRecord(message, duplicate, "Outbox"));
            return duplicate;
        }

        if (reservation.Decision == OutboxDecision.NeedsReview)
        {
            var unknown = OfferMessageResult.Unknown(
                $"invio precedente da verificare manualmente: {reservation.Detail}");
            Delivered?.Invoke(new OfferMessageRecord(message, unknown, "Outbox"));
            return unknown;
        }

        var primary = ResolvePrimary(config.Delivery);
        var result = await TryPrimaryAsync(message, config, primary, cancellationToken).ConfigureAwait(false);
        var channel = primary?.Name ?? "—";

        if (result is null || (result.Outcome != MessageOutcome.Sent && result.Outcome != MessageOutcome.Unknown))
        {
            // Nothing automatic worked (or it's disabled): make sure the seller can paste it.
            // When no automatic channel exists at all, always stage it so a pre-send error
            // does not make the composed message disappear. Unknown is intentionally not
            // copied: pasting it could produce the very duplicate this outbox prevents.
            var noAutomaticChannel = primary is not { IsReady: true };

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

        result ??= OfferMessageResult.Failed("nessun canale di invio disponibile");
        _outbox.Complete(reservation, result);
        Delivered?.Invoke(new OfferMessageRecord(message, result, channel));
        return result;
    }

    private IOfferMessenger? ResolvePrimary(MessageDelivery delivery) => delivery switch
    {
        MessageDelivery.PlaywrightBrowser => PlaywrightPrimary,
        MessageDelivery.AutoBrowser => BrowserPrimary,
        _ => null
    };

    /// <summary>Retries only failures proven to have happened before any send gesture.</summary>
    private async Task<OfferMessageResult?> TryPrimaryAsync(
        OutgoingOfferMessage message,
        OfferMessageSettings config,
        IOfferMessenger? primary,
        CancellationToken cancellationToken)
    {
        if (config.Delivery == MessageDelivery.ClipboardOnly)
        {
            return null;
        }

        if (primary is not { IsReady: true })
        {
            return OfferMessageResult.Failed(config.Delivery == MessageDelivery.PlaywrightBrowser
                ? "browser Playwright non pronto: apri la sessione di automazione e accedi a Eldorado"
                : "browser integrato non pronto (apri la scheda Chat e accedi)");
        }

        var attempts = Math.Max(1, config.MaxAttempts);
        OfferMessageResult last = OfferMessageResult.Failed("nessun tentativo eseguito");

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                last = await primary.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return OfferMessageResult.Failed("invio annullato");
            }
            catch (Exception ex)
            {
                // An unclassified exception can occur after the browser accepted the click.
                // Treat it as unknown rather than allowing an unsafe second send.
                last = OfferMessageResult.Unknown($"canale interrotto durante l'invio: {ex.Message}");
            }

            if (last.Outcome is MessageOutcome.Sent or MessageOutcome.Unknown || last.Permanent || !last.Retryable)
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
