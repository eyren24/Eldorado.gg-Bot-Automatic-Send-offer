using EldoradoApp.Models;

namespace EldoradoApp.Services;

public enum AutoOfferOutcome
{
    Submitted,
    DryRunWouldSubmit,
    SkippedRegion,
    SkippedRank,
    SkippedNoRange,
    Accepted,
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
/// The auto-bidding engine: polls received boosting requests, parses each one's
/// rank range + region from its category title, computes a price and delivery
/// time per division, and answers the allowed ones. Reports when a buyer accepts.
/// </summary>
/// <remarks>
/// Safety: nothing is submitted unless the settings are <c>Enabled</c> and not in
/// <c>DryRun</c>. Unparseable ranges are skipped and logged (with the raw title)
/// so the category parser can be calibrated against live data.
/// </remarks>
public sealed class AutoOfferEngine(
    IBoostingRequestClient requests,
    IBoostingOfferClient offers,
    Func<BoostingBotSettings> settingsProvider)
{
    private readonly HashSet<string> _offered = [];
    private readonly HashSet<string> _won = [];

    public event Action<AutoOfferEvent>? Activity;

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
        // The loop running (Start/Stop) is the on/off switch; no separate gate.
        var settings = settingsProvider();
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

            var title = request.BoostingCategoryTitle;
            var pricing = settings.ForCategory(request.GameId, request.BoostingCategoryId);

            // Not configured / disabled for this category → silently skip (keeps the log clean).
            if (pricing is null || !pricing.Enabled)
            {
                continue;
            }

            if (pricing.PricePerUnit <= 0)
            {
                Emit(AutoOfferOutcome.SkippedNoRange, request.Id, title, request.BuyerUsername, null,
                    $"Prezzo non impostato per la categoria \"{title}\"");
                continue;
            }

            var draft = new BoostingOfferDraft(
                BoostingRequestId: request.Id,
                DeliveryTime: pricing.DeliveryTime,
                PricePerUnit: pricing.PricePerUnit,
                Currency: settings.Currency,
                Quantity: Math.Max(1, pricing.Quantity),
                MinQuantity: Math.Max(1, pricing.MinQuantity));

            var note = string.IsNullOrWhiteSpace(settings.OfferMessage)
                ? ""
                : $" · 📝 \"{settings.OfferMessage.Trim()}\"";
            var summary = $"{title}: {pricing.PricePerUnit:N2} {settings.Currency}/unità " +
                          $"(qta {draft.MinQuantity}-{draft.Quantity}) → {pricing.DeliveryTime}{note}";

            if (settings.DryRun)
            {
                _offered.Add(request.Id);
                Emit(AutoOfferOutcome.DryRunWouldSubmit, request.Id, title, request.BuyerUsername, pricing.PricePerUnit,
                    $"[DRY-RUN] {summary}");
                continue;
            }

            try
            {
                await offers.SubmitOfferAsync(draft, cancellationToken).ConfigureAwait(false);
                _offered.Add(request.Id);
                Emit(AutoOfferOutcome.Submitted, request.Id, title, request.BuyerUsername, pricing.PricePerUnit,
                    $"Offerta inviata: {summary}");
            }
            catch (Exception ex)
            {
                // Leave it un-offered so the next cycle retries.
                Emit(AutoOfferOutcome.Error, request.Id, title, request.BuyerUsername, pricing.PricePerUnit,
                    $"Invio fallito: {ex.Message}");
            }
        }
    }

    private async Task DetectAcceptedAsync(BoostingBotSettings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<BoostingRequest> won;
        try
        {
            won = await requests
                .GetReceivedRequestsAsync(BoostingRequestFilter.OfferWon, settings.GameId, 50, cancellationToken)
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
        => Activity?.Invoke(new AutoOfferEvent(outcome, requestId, title, buyer, price, message, DateTimeOffset.Now));
}
