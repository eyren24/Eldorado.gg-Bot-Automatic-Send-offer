using System.IO;
using System.Text.Json;
using EldoradoApp.Models;
using Microsoft.Playwright;

namespace EldoradoApp.Services;

/// <summary>
/// Reliable post-offer chat sender backed by a dedicated persistent Edge profile.
///
/// Unlike WebView2 script injection, Playwright can address cross-origin frames directly,
/// wait for locators to be actionable, and use the browser-native file-input path. Each
/// send gesture is performed once only; a missing confirmation becomes <see cref="MessageOutcome.Unknown"/>
/// so the dispatcher cannot create a duplicate on a retry.
/// </summary>
public sealed class PlaywrightOfferMessenger(Func<OfferMessageSettings> settingsProvider) : IOfferMessenger, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private string? _profilePath;
    private string _status = "Browser Playwright non avviato";

    public string Name => "Playwright · Edge";

    /// <summary>The session starts lazily at first delivery or when the user presses "Apri automazione".</summary>
    public bool IsReady => settingsProvider().Playwright is { Enabled: true };

    public string Status => _status;
    public event Action? StatusChanged;

    /// <summary>Shows the persistent automation browser so the seller can complete normal website login once.</summary>
    public async Task OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = settingsProvider();
            settings.Playwright.Normalize();
            var page = await EnsurePageAsync(settings, cancellationToken).ConfigureAwait(false);
            await page.GotoAsync(settings.ChatUrl, GotoOptions(settings.Playwright)).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            SetStatus("Browser Playwright aperto: accedi a Eldorado in questa finestra se necessario.");
        }
        catch (Exception ex)
        {
            SetStatus($"Browser Playwright non avviato: {ex.Message}");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfferMessageResult> SendAsync(
        OutgoingOfferMessage message,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsProvider();
        settings.Playwright.Normalize();
        if (!settings.Playwright.Enabled)
        {
            return OfferMessageResult.Failed("canale Playwright disattivato nelle impostazioni");
        }

        if (string.IsNullOrWhiteSpace(message.BuyerUsername))
        {
            return OfferMessageResult.Gone("richiesta senza nome del compratore: non apro una chat non verificabile");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus("Apro la conversazione del compratore…");
            var page = await EnsurePageAsync(settings, cancellationToken).ConfigureAwait(false);
            var target = await OpenConversationAsync(page, message, settings, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                return OfferMessageResult.RetryableFailure(
                    "chat non pronta: apri il browser Playwright, completa il login e verifica l'URL della conversazione");
            }

            if (settings.StrictBuyerMatch && !await IsBuyerMatchAsync(target, message.BuyerUsername!, cancellationToken).ConfigureAwait(false))
            {
                return OfferMessageResult.Failed(
                    $"non riesco a confermare che la chat sia di «{message.BuyerUsername}»: non invio nulla");
            }

            var parts = OfferMessageComposer.Split(message.Text, settings.SplitMessages);
            if (parts.Count == 0)
            {
                return OfferMessageResult.Failed("nessun testo da inviare");
            }

            for (var index = 0; index < parts.Count; index++)
            {
                if (index > 0 && settings.BetweenMessagesMs > 0)
                {
                    await Task.Delay(Math.Clamp(settings.BetweenMessagesMs, 0, 10_000), cancellationToken).ConfigureAwait(false);
                }

                target = await FindChatAsync(page, settings.Playwright, settings.Playwright.ActionTimeoutMs, cancellationToken)
                    .ConfigureAwait(false) ?? target;
                var result = await SendTextOnceAsync(target, parts[index], settings.Playwright, cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                {
                    var suffix = index == 0 ? "" : $" (i primi {index} di {parts.Count} risultano già inviati)";
                    return result with { Detail = result.Detail + suffix };
                }
            }

            if (settings.AttachBanner && message.HasBanner)
            {
                target = await FindChatAsync(page, settings.Playwright, settings.Playwright.ActionTimeoutMs, cancellationToken)
                    .ConfigureAwait(false) ?? target;
                var banner = await SendBannerOnceAsync(page, target, message.BannerPath!, settings.Playwright, cancellationToken)
                    .ConfigureAwait(false);
                if (banner is not null)
                {
                    return banner with { Detail = banner.Detail + " · testo già confermato" };
                }
            }

            var detail = message.HasBanner && settings.AttachBanner
                ? $"inviato a {message.BuyerUsername} con banner"
                : $"inviato a {message.BuyerUsername}";
            SetStatus("Messaggio confermato dalla conversazione.");
            return OfferMessageResult.Sent(detail);
        }
        catch (OperationCanceledException)
        {
            return OfferMessageResult.Failed("invio Playwright annullato");
        }
        catch (PlaywrightException ex)
        {
            ApiLog.Write($"[playwright] {ex.Message}");
            SetStatus($"Errore Playwright: {ex.Message}");
            return OfferMessageResult.RetryableFailure($"browser Playwright: {ex.Message}");
        }
        catch (Exception ex)
        {
            ApiLog.Write($"[playwright] unexpected: {ex}");
            SetStatus($"Errore Playwright: {ex.Message}");
            return OfferMessageResult.Failed($"browser Playwright: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IPage> EnsurePageAsync(OfferMessageSettings settings, CancellationToken cancellationToken)
    {
        var options = settings.Playwright;
        var profile = ResolveProfilePath(options);
        if (_context is not null && !string.Equals(profile, _profilePath, StringComparison.OrdinalIgnoreCase))
        {
            await _context.CloseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            _context = null;
            _page = null;
        }

        if (_context is null)
        {
            _playwright ??= await Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(profile);
            SetStatus("Avvio Edge controllato da Playwright…");
            _context = await _playwright.Chromium.LaunchPersistentContextAsync(profile,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Channel = options.BrowserChannel,
                    Headless = options.Headless
                }).WaitAsync(cancellationToken).ConfigureAwait(false);
            _context.SetDefaultTimeout(options.ActionTimeoutMs);
            _profilePath = profile;
        }

        _page ??= _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync().WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return _page;
    }

    private async Task<ChatTarget?> OpenConversationAsync(
        IPage page,
        OutgoingOfferMessage message,
        OfferMessageSettings settings,
        CancellationToken cancellationToken)
    {
        var link = DeepLink(settings.ConversationUrl, message);
        if (link is not null)
        {
            await page.GotoAsync(link, GotoOptions(settings.Playwright)).WaitAsync(cancellationToken).ConfigureAwait(false);
            var chat = await FindChatAsync(page, settings.Playwright, settings.Playwright.ActionTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (chat is not null)
            {
                return chat;
            }

            // On some request pages the panel is mounted only after the visible "chat" action.
            var opener = await FindActionAsync(page.Frames, settings.Playwright.AttachButtonSelector,
                ["chat", "message", "talk", "scrivi"], cancellationToken).ConfigureAwait(false);
            if (opener is not null)
            {
                await opener.ClickAsync(new LocatorClickOptions { Timeout = settings.Playwright.ActionTimeoutMs })
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                return await FindChatAsync(page, settings.Playwright, settings.Playwright.VerificationTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
            }

            return null;
        }

        // The explicit fallback is deliberately conservative: without the request deep link,
        // reliably selecting a just-created TalkJS conversation is not possible everywhere.
        await page.GotoAsync(settings.ChatUrl, GotoOptions(settings.Playwright)).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return await FindChatAsync(page, settings.Playwright, settings.Playwright.ActionTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ChatTarget?> FindChatAsync(
        IPage page,
        PlaywrightMessageOptions options,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var frame in page.Frames)
            {
                try
                {
                    var boxes = frame.Locator(options.ComposerSelector);
                    var count = await boxes.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                    for (var index = 0; index < count; index++)
                    {
                        var composer = boxes.Nth(index);
                        if (await composer.IsVisibleAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return new ChatTarget(frame, composer);
                        }
                    }
                }
                catch (PlaywrightException)
                {
                    // A TalkJS frame can be replaced while it is being inspected; the next
                    // poll gets the new frame rather than retaining a stale handle.
                }
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<OfferMessageResult?> SendTextOnceAsync(
        ChatTarget target,
        string text,
        PlaywrightMessageOptions options,
        CancellationToken cancellationToken)
    {
        if (await ContainsDeliveredTextAsync(target, text, cancellationToken).ConfigureAwait(false))
        {
            return null; // Already in the visible conversation: idempotent success.
        }

        try
        {
            await target.Composer.FillAsync(text, new LocatorFillOptions { Timeout = options.ActionTimeoutMs })
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            return OfferMessageResult.RetryableFailure($"non riesco a scrivere nella chat: {ex.Message}");
        }

        var send = await FindActionAsync([target.Frame], options.SendButtonSelector, ["send", "invia"], cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (send is not null)
            {
                // Exactly one gesture. There is deliberately no Enter-plus-click fallback.
                await send.ClickAsync(new LocatorClickOptions { Timeout = options.ActionTimeoutMs })
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await target.Composer.PressAsync("Enter", new LocatorPressOptions { Timeout = options.ActionTimeoutMs })
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PlaywrightException ex)
        {
            return OfferMessageResult.Unknown($"gesto di invio eseguito ma non confermabile: {ex.Message}");
        }

        var delivered = await WaitForDeliveredTextAsync(target, text, options.VerificationTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        return delivered
            ? null
            : OfferMessageResult.Unknown("cliccato Invia una sola volta, ma la chat non ha confermato il messaggio");
    }

    private static async Task<OfferMessageResult?> SendBannerOnceAsync(
        IPage page,
        ChatTarget target,
        string path,
        PlaywrightMessageOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return OfferMessageResult.Failed("banner non trovato sul disco");
        }

        var baseline = await ImageCountAsync(target.Frame, cancellationToken).ConfigureAwait(false);
        var uploaded = false;
        try
        {
            var inputs = target.Frame.Locator(options.FileInputSelector);
            var count = await inputs.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                await inputs.First.SetInputFilesAsync(path, new LocatorSetInputFilesOptions { Timeout = options.ActionTimeoutMs })
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                uploaded = true;
            }
            else
            {
                var attach = await FindActionAsync([target.Frame], options.AttachButtonSelector,
                    ["attach", "allega", "upload", "image", "foto"], cancellationToken).ConfigureAwait(false);
                if (attach is not null)
                {
                    var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
                        await attach.ClickAsync(new LocatorClickOptions { Timeout = options.ActionTimeoutMs }))
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    await chooser.SetFilesAsync(path).WaitAsync(cancellationToken).ConfigureAwait(false);
                    uploaded = true;
                }
            }
        }
        catch (PlaywrightException ex)
        {
            return OfferMessageResult.RetryableFailure($"upload banner non avviato: {ex.Message}");
        }

        if (!uploaded)
        {
            return OfferMessageResult.RetryableFailure("campo file o pulsante allega non trovato nella chat");
        }

        if (await WaitForNewImageAsync(target.Frame, baseline, options.VerificationTimeoutMs, cancellationToken).ConfigureAwait(false))
        {
            return null; // TalkJS has already posted the attachment itself.
        }

        var send = await FindActionAsync([target.Frame], options.SendButtonSelector, ["send", "invia"], cancellationToken)
            .ConfigureAwait(false);
        if (send is null)
        {
            return OfferMessageResult.Unknown("banner caricato ma non trovo un invio sicuro da premere");
        }

        try
        {
            await send.ClickAsync(new LocatorClickOptions { Timeout = options.ActionTimeoutMs })
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            return OfferMessageResult.Unknown($"pressione invio banner non confermabile: {ex.Message}");
        }

        return await WaitForNewImageAsync(target.Frame, baseline, options.VerificationTimeoutMs, cancellationToken)
            .ConfigureAwait(false)
            ? null
            : OfferMessageResult.Unknown("banner inviato una volta ma non confermato nella conversazione");
    }

    private static async Task<bool> IsBuyerMatchAsync(ChatTarget target, string buyer, CancellationToken cancellationToken)
    {
        try
        {
            var text = await target.Frame.Locator("body").InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return text.Contains(buyer, StringComparison.OrdinalIgnoreCase);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<ILocator?> FindActionAsync(
        IEnumerable<IFrame> frames,
        string selector,
        IEnumerable<string> words,
        CancellationToken cancellationToken)
    {
        var wanted = words.ToArray();
        foreach (var frame in frames)
        {
            try
            {
                var candidates = frame.Locator(selector);
                var count = await candidates.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < count; index++)
                {
                    var item = candidates.Nth(index);
                    if (!await item.IsVisibleAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var label = string.Join(" ", new[]
                    {
                        await SafeInnerTextAsync(item, cancellationToken).ConfigureAwait(false),
                        await item.GetAttributeAsync("aria-label").WaitAsync(cancellationToken).ConfigureAwait(false),
                        await item.GetAttributeAsync("title").WaitAsync(cancellationToken).ConfigureAwait(false),
                        await item.GetAttributeAsync("data-testid").WaitAsync(cancellationToken).ConfigureAwait(false)
                    }.Where(x => !string.IsNullOrWhiteSpace(x)));

                    if (wanted.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        return item;
                    }
                }
            }
            catch (PlaywrightException)
            {
                // The caller will retry with the fresh frame if it was remounted.
            }
        }

        return null;
    }

    private static async Task<bool> ContainsDeliveredTextAsync(ChatTarget target, string text, CancellationToken cancellationToken)
    {
        try
        {
            var body = await target.Frame.Locator("body").InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var composer = await ReadComposerAsync(target.Composer, cancellationToken).ConfigureAwait(false);
            return body.Contains(text, StringComparison.Ordinal) && !composer.Contains(text, StringComparison.Ordinal);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForDeliveredTextAsync(
        ChatTarget target,
        string text,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await ContainsDeliveredTextAsync(target, text, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<int> ImageCountAsync(IFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            return await frame.Locator("img, video, [style*='background-image']").CountAsync()
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return -1;
        }
    }

    private static async Task<bool> WaitForNewImageAsync(
        IFrame frame,
        int baseline,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (baseline < 0)
        {
            return false;
        }

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await ImageCountAsync(frame, cancellationToken).ConfigureAwait(false) > baseline)
            {
                return true;
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<string> ReadComposerAsync(ILocator composer, CancellationToken cancellationToken)
    {
        try
        {
            return await composer.InputValueAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            try
            {
                return await composer.InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                return "";
            }
        }
    }

    private static async Task<string?> SafeInnerTextAsync(ILocator locator, CancellationToken cancellationToken)
    {
        try
        {
            return await locator.InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private static string? DeepLink(string? template, OutgoingOfferMessage message)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        return template.Trim()
            .Replace("{requestId}", Uri.EscapeDataString(message.RequestId))
            .Replace("{buyer}", Uri.EscapeDataString(message.BuyerUsername ?? ""))
            .Replace("{buyerId}", Uri.EscapeDataString(message.BuyerId ?? ""));
    }

    private static PageGotoOptions GotoOptions(PlaywrightMessageOptions options) => new()
    {
        WaitUntil = WaitUntilState.DOMContentLoaded,
        Timeout = options.ActionTimeoutMs
    };

    private static string ResolveProfilePath(PlaywrightMessageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ProfilePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.ProfilePath));
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EldoradoApp", "playwright-edge-profile");
    }

    private void SetStatus(string value)
    {
        _status = value;
        StatusChanged?.Invoke();
        ApiLog.Write($"[playwright] {value}");
    }

    public void Dispose()
    {
        try
        {
            _context?.CloseAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // The application is shutting down; Chromium may already be gone.
        }

        _playwright?.Dispose();
        _gate.Dispose();
    }

    private sealed record ChatTarget(IFrame Frame, ILocator Composer);
}
