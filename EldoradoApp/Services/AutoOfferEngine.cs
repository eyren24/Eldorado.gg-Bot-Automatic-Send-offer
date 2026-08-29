using EldoradoApp.Models;

namespace EldoradoApp.Services;

public enum AutoOfferOutcome
{
    Submitted,
    DryRunWouldSubmit,
    SkippedRegion,
    SkippedCategory,
    SkippedNoRange,
    Accepted,
    Message,
    Info,
    Error
}

/// <summary>A single thing the engine did (or would have done) in a poll cycle.</summary>
public sealed record AutoOfferEvent(
    AutoOfferOutcome Outcome,
    string RequestId,
    string? CategoryTitle,
    string? BuyerUsername,
    decimal? Price,
    string Message,
    DateTimeOffset Timestamp);

/// <summary>
/// The auto-bidding engine: polls received boosting requests, prices each one on the
/// rank ladder (base price + one surcharge per division climbed + the buyer's extras),
/// answers the allowed ones and — the moment an offer lands — fires the seller's
/// message with its banner. Reports when a buyer accepts.
/// </summary>
/// <remarks>
/// Safety: nothing is submitted while <c>DryRun</c> is on. Requests whose rank range
/// can't be parsed are skipped and logged with the raw title, so the parser can be
/// calibrated against live data instead of guessing a price.
/// </remarks>
public sealed class AutoOfferEngine(
    IBoostingRequestClient requests,
    IBoostingOfferClient offers,
    Func<BoostingBotSettings> settingsProvider,
    OfferMessageDispatcher? messages = null)
{
    private readonly HashSet<string> _offered = [];
    private readonly HashSet<string> _won = [];

    public event Action<AutoOfferEvent>? Activity;

    /// <summary>Requests answered since the app started (used by the dashboard counters).</summary>
    public int OfferedCount => _offered.Count;

    public int WonCount => _won.Count;

    public async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);

            var seconds = Math.Max(5, settingsProvider().PollIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsProvider().Normalized();
        await ProcessActiveRequestsAsync(settings, cancellationToken).ConfigureAwait(false);
        await DetectAcceptedAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessActiveRequestsAsync(BoostingBotSettings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<BoostingRequest> active;
        try
        {
            active = await requests
                .GetReceivedRequestsAsync(BoostingRequestFilter.ActiveRequests, settings.GameId, 50, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Emit(AutoOfferOutcome.Error, "-", null, null, null, $"Errore lettura richieste: {ex.Message}");
            return;
        }

        // Heartbeat so the log shows the bot is alive even when nothing matches.
        Emit(AutoOfferOutcome.Info, "-", null, null, null,
            $"Controllo: {active.Count} richieste attive{(settings.DryRun ? " · DRY-RUN" : "")}");

        foreach (var request in active)
        {
            if (_offered.Contains(request.Id))
            {
                continue;
            }

            await ProcessOneAsync(request, settings, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessOneAsync(
        BoostingRequest request, BoostingBotSettings settings, CancellationToken cancellationToken)
    {
        var title = request.BoostingCategoryTitle;
        var category = settings.ForCategory(request.GameId, request.BoostingCategoryId);

        if (category is null && !settings.AnswerUnknownCategories)
        {
            return;
        }

        if (category is { Enabled: false })
        {
            return;
        }

        // Valorant only: never bid on another game, whatever the feed returns.
        if (settings.ValorantOnly && settings.GameId is { Length: > 0 } valorantId &&
            !string.Equals(request.GameId, valorantId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var parsed = BoostingCategoryParser.Parse(request, settings);
        if (!settings.IsRegionAccepted(parsed.Region))
        {
            Emit(AutoOfferOutcome.SkippedRegion, request.Id, title, request.BuyerUsername, null,
                $"Regione {parsed.Region ?? "?"} esclusa");
            return;
        }

        var quote = BoostingPriceCalculator.Quote(request, settings);

        if (!quote.IsPriceable)
        {
            if (settings.SkipUnparsableRanges)
            {
                Emit(AutoOfferOutcome.SkippedNoRange, request.Id, title, request.BuyerUsername, null,
                    $"Non quotabile ({quote.Problem}) · titolo grezzo: \"{title}\"");
                return;
            }

            // The seller chose to bid anyway: base price + extras, no division surcharges.
            quote = BoostingPriceCalculator.QuoteWithoutRange(parsed.MatchedExtraIds, settings, parsed.Region);

            if (!quote.IsPriceable)
            {
                Emit(AutoOfferOutcome.SkippedNoRange, request.Id, title, request.BuyerUsername, null,
                    "Prezzo base a 0: imposta un prezzo base per offrire senza range");
                return;
            }
        }

        var deliveryTime = settings.DeliveryTimeFor(request.GameId, request.BoostingCategoryId);
        var (pricePerUnit, quantity, minQuantity) =
            BoostingPriceCalculator.ToOfferPricing(quote, settings, category);

        var draft = new BoostingOfferDraft(
            BoostingRequestId: request.Id,
            DeliveryTime: deliveryTime,
            PricePerUnit: pricePerUnit,
            Currency: settings.Currency,
            Quantity: quantity,
            MinQuantity: minQuantity);

        var summary = $"{quote.Summary} → {deliveryTime}" +
                      (quantity > 1 ? $" (qta {minQuantity}-{quantity} × {pricePerUnit:N2})" : "");

        if (settings.DryRun)
        {
            _offered.Add(request.Id);
            Emit(AutoOfferOutcome.DryRunWouldSubmit, request.Id, title, request.BuyerUsername, quote.Total,
                $"[DRY-RUN] {summary}");
            return;
        }

        try
        {
            await offers.SubmitOfferAsync(draft, cancellationToken).ConfigureAwait(false);
            _offered.Add(request.Id);
            Emit(AutoOfferOutcome.Submitted, request.Id, title, request.BuyerUsername, quote.Total,
                $"Offerta inviata: {summary}");
        }
        catch (Exception ex)
        {
            // Leave it un-offered so the next cycle retries.
            Emit(AutoOfferOutcome.Error, request.Id, title, request.BuyerUsername, quote.Total,
                $"Invio fallito: {ex.Message}");
            return;
        }

        await SendFollowUpAsync(request, quote, deliveryTime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fires the seller's message + banner right after the offer landed.</summary>
    private async Task SendFollowUpAsync(
        BoostingRequest request,
        PriceQuote quote,
        BoostingDeliveryTime deliveryTime,
        CancellationToken cancellationToken)
    {
        if (messages is null)
        {
            return;
        }

        try
        {
            var result = await messages
                .DispatchAsync(request, quote, deliveryTime, cancellationToken)
                .ConfigureAwait(false);

            if (result.Outcome != MessageOutcome.Disabled)
            {
                var icon = result.Outcome switch
                {
                    MessageOutcome.Sent => "💬",
                    MessageOutcome.Staged => "📋",
                    _ => "⚠️"
                };

                Emit(result.Outcome == MessageOutcome.Failed ? AutoOfferOutcome.Error : AutoOfferOutcome.Message,
                    request.Id, request.BoostingCategoryTitle, request.BuyerUsername, null,
                    $"{icon} Messaggio: {result.Detail}");
            }
        }
        catch (Exception ex)
        {
            Emit(AutoOfferOutcome.Error, request.Id, request.BoostingCategoryTitle, request.BuyerUsername, null,
                $"Messaggio automatico fallito: {ex.Message}");
        }
    }

    private async Task DetectAcceptedAsync(BoostingBotSettings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<BoostingRequest> won;
        try
        {
            // Won requests are only counted and announced, never priced: skip the detail fetch.
            won = await requests
                .GetReceivedRequestsAsync(
                    BoostingRequestFilter.OfferWon, settings.GameId, 50, cancellationToken, hydrate: false)
                .ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        foreach (var request in won)
        {
            if (_won.Add(request.Id))
            {
                Emit(AutoOfferOutcome.Accepted, request.Id, request.BoostingCategoryTitle, request.BuyerUsername, null,
                    $"Offerta accettata da {request.BuyerUsername ?? "buyer"} 🎉");
            }
        }
    }

    private void Emit(AutoOfferOutcome outcome, string requestId, string? title, string? buyer, decimal? price, string message)
    {
        // The in-app activity list is capped and dies with the process; the file survives,
        // so a bad night can actually be read back instead of screenshotted.
        ApiLog.Write($"[bot] {outcome} req={requestId} buyer={buyer ?? "-"} :: {message}");

        Activity?.Invoke(new AutoOfferEvent(outcome, requestId, title, buyer, price, message, DateTimeOffset.Now));
    }
}
