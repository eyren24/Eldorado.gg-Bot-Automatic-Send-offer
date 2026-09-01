using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EldoradoApp.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EldoradoApp.Services;

/// <summary>
/// Sends the post-offer message by driving the Eldorado chat inside the app's own browser tab.
/// </summary>
/// <remarks>
/// The seller API exposes no messaging endpoint, so posting as the seller means acting on
/// the logged-in web session. What matters here is <b>who</b> gets the message: the
/// messenger opens the buyer's conversation itself — inbox, then their row in the list,
/// falling back to the inbox filter when the list is paginated — and confirms the open
/// conversation is theirs before typing a single character. When it cannot open or cannot
/// confirm it, it refuses and lets <see cref="ClipboardOfferMessenger"/> stage the message
/// instead of writing to whoever happened to be on screen.
/// <para>
/// The chat is third-party markup in a (possibly nested) cross-origin iframe, so every
/// frame is tracked and each step is injected into all of them; the JavaScript itself lives
/// in <see cref="ChatScripts"/> and the typing step can still be overridden from the UI via
/// <see cref="OfferMessageSettings.ChatScript"/>.
/// </para>
/// </remarks>
public sealed class ChatBrowserMessenger(
    WebView2 browser, Dispatcher dispatcher, Func<OfferMessageSettings> config) : IOfferMessenger
{
    /// <summary>Time the page gets to switch conversation after a click.</summary>
    private const int SettleMs = 500;

    /// <summary>Time the chat gets to accept a send gesture before it is checked.</summary>
    private const int SendMs = 350;

    /// <summary>How long a single injected step may take before it is given up on.</summary>
    private const int ScriptTimeoutMs = 10_000;

    private readonly List<CoreWebView2Frame> _frames = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _hooked;

    public string Name => "Browser integrato";

    public bool IsReady => dispatcher.Invoke(() => browser.CoreWebView2 is not null);

    /// <summary>Starts tracking frames, nested ones included, so scripts reach the chat widget.</summary>
    public void Attach()
    {
        if (_hooked || browser.CoreWebView2 is null)
        {
            return;
        }

        _hooked = true;
        browser.CoreWebView2.FrameCreated += (_, e) => Track(e.Frame);
    }

    private void Track(CoreWebView2Frame frame)
    {
        lock (_frames)
        {
            _frames.Add(frame);
        }

        frame.Destroyed += (sender, _) =>
        {
            if (sender is CoreWebView2Frame gone)
            {
                lock (_frames)
                {
                    _frames.Remove(gone);
                }
            }
        };

        // The chat composer usually lives one iframe deeper than the widget's own frame.
        try
        {
            frame.FrameCreated += (_, e) => Track(e.Frame);
        }
        catch (NotImplementedException)
        {
            // Older WebView2 runtime: top-level frames only.
        }
    }

    public async Task<OfferMessageResult> SendAsync(
        OutgoingOfferMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            return OfferMessageResult.Failed("browser integrato non inizializzato");
        }

        var buyer = (message.BuyerUsername ?? "").Trim();
        if (buyer.Length == 0)
        {
            return OfferMessageResult.Failed("richiesta senza nome del compratore: non so quale chat aprire");
        }

        // One conversation at a time: two offers landing together would fight over the page.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = config();
            var result = await DeliverAsync(message, buyer, settings, cancellationToken).ConfigureAwait(false);

            // A failed delivery can leave the browser parked on whatever the site redirected
            // to. Put it back on the inbox so the Chat tab stays usable and the next message
            // does not start from a stranded page.
            if (result.Outcome != MessageOutcome.Sent)
            {
                await NavigateAsync(settings.ChatUrl, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return OfferMessageResult.Failed("invio annullato");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OfferMessageResult> DeliverAsync(
        OutgoingOfferMessage message, string buyer, OfferMessageSettings settings, CancellationToken ct)
    {
        var notes = new List<string>();

        var opened = await OpenConversationAsync(message, buyer, settings, notes, ct).ConfigureAwait(false);
        if (opened.Failure is { } failure)
        {
            return failure;
        }

        var chat = opened.Chat!;

        // One chat message per block of the template, sent in order.
        var parts = OfferMessageComposer.Split(message.Text, settings.SplitMessages);
        if (parts.Count == 0)
        {
            return OfferMessageResult.Failed("nessun testo da inviare");
        }

        // The banner leads. It is the thing that makes the buyer stop scrolling, so it has
        // to be above the pitch, not below it — and sending it while the composer is still
        // empty keeps the upload from racing a half-typed message.
        if (settings.AttachBanner && message.HasBanner)
        {
            notes.Add(await AttachBannerAsync(chat, message.BannerPath!, ct).ConfigureAwait(false));
        }

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0 && settings.BetweenMessagesMs > 0)
            {
                // A pause between messages: they arrive as a person writing them, and the
                // chat gets time to clear its composer before the next one is typed in.
                await Task.Delay(Math.Clamp(settings.BetweenMessagesMs, 0, 10_000), ct).ConfigureAwait(false);
            }

            var written = await WriteAsync(chat, parts[i], settings, ct).ConfigureAwait(false);
            if (written is { } textFailure)
            {
                // Say how far it got: with several messages, "not sent" alone would hide
                // that the buyer already has the first half of the pitch.
                var sent = i == 0 ? "" : $" (i primi {i} di {parts.Count} sono partiti)";
                return textFailure with { Detail = textFailure.Detail + sent };
            }
        }

        if (parts.Count > 1)
        {
            notes.Add($"{parts.Count} messaggi");
        }

        var detail = notes.Count == 0 ? "" : " · " + string.Join(" · ", notes);
        return OfferMessageResult.Sent($"inviato a {buyer}{detail}");
    }

    /// <summary>Brings the buyer's conversation on screen and proves it is really theirs.</summary>
    private async Task<(Target? Chat, OfferMessageResult? Failure)> OpenConversationAsync(
        OutgoingOfferMessage message, string buyer, OfferMessageSettings settings, List<string> notes,
        CancellationToken ct)
    {
        // The page of the request the offer answers, when the seller configured one: the
        // conversation usually does not exist yet at this point — there is no row to click
        // in the inbox — and the site's own "chat with the buyer" button is what creates it.
        var link = DeepLink(settings, message);
        List<Probed> probes;

        if (link is not null)
        {
            var landed = await NavigateAsync(link, ct).ConfigureAwait(false);
            if (!StillOnRequest(landed, link))
            {
                return (null, OfferMessageResult.Gone(
                    $"la richiesta non esiste piu': la pagina e' finita su {Shorten(landed)}"));
            }

            probes = await ProbeAsync(buyer, ct).ConfigureAwait(false);
            if (probes.Any(p => p.HasComposer))
            {
                notes.Add("link diretto");
            }
            else
            {
                var pressed = await RunEverywhereAsync(ChatScripts.Compose(ChatScripts.ClickChat, buyer: buyer), ct)
                    .ConfigureAwait(false);
                if (pressed is null)
                {
                    return (null, OfferMessageResult.Failed(
                        $"su {Shorten(link)} non c'e' ne' la chat ne' un pulsante per aprirla"));
                }

                // The button opens the conversation right here, so this is where the message
                // gets written — no going back to the request, and no inbox to reach.
                //
                // Hunting for a conversation list at this point was costing the full 15 s
                // timeout on every single message: the chat that button opens has neither a
                // list nor a filter, so the inbox wait could only ever run out.
                probes = await WaitForComposerAsync(buyer, ct).ConfigureAwait(false);
                notes.Add($"chat aperta dalla richiesta ({Reason(pressed)})");
            }
        }
        else
        {
            if (!IsInbox(dispatcher.Invoke(() => browser.Source?.ToString() ?? ""), settings.ChatUrl))
            {
                await NavigateAsync(settings.ChatUrl, ct).ConfigureAwait(false);
            }

            probes = await WaitForInboxAsync(buyer, ct).ConfigureAwait(false);
            if (probes.Count == 0)
            {
                return (null, OfferMessageResult.Failed(
                    "la pagina della chat non risponde: apri la scheda Chat e accedi a Eldorado"));
            }

            var failure = await SelectRowAsync(buyer, probes, notes, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return (null, failure);
            }

            // Opening a conversation rebuilds the composer, and can move it to another frame.
            probes = await ProbeAsync(buyer, ct).ConfigureAwait(false);
        }

        var chat = probes.FirstOrDefault(p => p.HasComposer);
        if (chat is null)
        {
            return (null, OfferMessageResult.Failed(
                "la casella messaggi non è comparsa dopo aver aperto la conversazione"));
        }

        var verdict = await EvalAsync(chat.Target, ChatScripts.Compose(ChatScripts.Verify, buyer: buyer), ct)
            .ConfigureAwait(false);
        var state = verdict is null ? "unknown" : Text(verdict.Value, "state");
        var header = verdict is null ? "" : Text(verdict.Value, "header");

        if (state == "mismatch")
        {
            return (null, OfferMessageResult.Failed(
                $"la chat aperta è di «{Text(verdict!.Value, "other")}», non di «{buyer}»: non scrivo niente"));
        }

        if (state != "match")
        {
            if (settings.StrictBuyerMatch)
            {
                return (null, OfferMessageResult.Failed(
                    $"non riesco a confermare che la chat aperta sia di «{buyer}» (intestazione: \"{header}\")"));
            }

            notes.Add("destinatario non verificato");
        }

        return (chat.Target, null);
    }

    /// <summary>Clicks the buyer's row, filtering the inbox first when they aren't listed yet.</summary>
    private async Task<OfferMessageResult?> SelectRowAsync(
        string buyer, List<Probed> probes, List<string> notes, CancellationToken ct)
    {
        var list = probes.FirstOrDefault(p => p.HasRow);

        if (list is null)
        {
            var filter = probes.FirstOrDefault(p => p.HasSearch);
            if (filter is null)
            {
                return OfferMessageResult.Failed(
                    $"«{buyer}» non è tra le conversazioni e la lista non ha un campo di ricerca");
            }

            var typed = await EvalAsync(filter.Target, ChatScripts.Compose(ChatScripts.Filter, buyer: buyer), ct)
                .ConfigureAwait(false);
            if (typed is null || !Flag(typed.Value, "ok"))
            {
                return OfferMessageResult.Failed($"ricerca di «{buyer}» non riuscita ({Reason(typed)})");
            }

            await Task.Delay(1500, ct).ConfigureAwait(false);
            probes = await ProbeAsync(buyer, ct).ConfigureAwait(false);
            list = probes.FirstOrDefault(p => p.HasRow);
            notes.Add("trovata con la ricerca");
        }

        if (list is null)
        {
            return OfferMessageResult.Failed(
                $"nessuna conversazione con «{buyer}»: il compratore non ha ancora aperto la chat");
        }

        var selected = await EvalAsync(list.Target, ChatScripts.Compose(ChatScripts.Select, buyer: buyer), ct)
            .ConfigureAwait(false);
        if (selected is null || !Flag(selected.Value, "ok"))
        {
            return OfferMessageResult.Failed($"chat di «{buyer}» non apribile ({Reason(selected)})");
        }

        await Task.Delay(SettleMs, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Types the message and makes sure the composer is empty afterwards.</summary>
    private async Task<OfferMessageResult?> WriteAsync(
        Target chat, string text, OfferMessageSettings settings, CancellationToken ct)
    {
        // A custom script from the settings takes over the whole typing step.
        var custom = (settings.ChatScript ?? "").Trim();
        if (custom.Length > 0)
        {
            var result = await EvalAsync(chat, custom.Replace("__TEXT__", JsonSerializer.Serialize(text)), ct)
                .ConfigureAwait(false);
            return result is not null && Flag(result.Value, "ok")
                ? null
                : OfferMessageResult.Failed($"script personalizzato: {Reason(result)}");
        }

        var written = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Write, text: text), ct)
            .ConfigureAwait(false);
        if (written is null || !Flag(written.Value, "ok"))
        {
            return OfferMessageResult.Failed($"testo non scritto ({Reason(written)})");
        }

        await SendGestureAsync(chat, ct).ConfigureAwait(false);

        return await PendingAsync(chat, ct).ConfigureAwait(false) > 0
            ? OfferMessageResult.Failed("testo scritto ma non inviato: la chat non ha reagito a Invio")
            : null;
    }

    /// <summary>Enter first; the send button only if something is still sitting in the box.</summary>
    private async Task SendGestureAsync(Target chat, CancellationToken ct)
    {
        await Task.Delay(120, ct).ConfigureAwait(false);
        await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Submit), ct).ConfigureAwait(false);
        await Task.Delay(SendMs, ct).ConfigureAwait(false);

        if (await PendingAsync(chat, ct).ConfigureAwait(false) > 0)
        {
            await EvalAsync(chat, ChatScripts.Compose(ChatScripts.ClickSend), ct).ConfigureAwait(false);
            await Task.Delay(SendMs, ct).ConfigureAwait(false);
        }
    }

    private async Task<int> PendingAsync(Target chat, CancellationToken ct)
    {
        var pending = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Pending), ct).ConfigureAwait(false);
        return pending is null ? 0 : Number(pending.Value, "pending");
    }

    /// <summary>
    /// Puts the banner in the composer and sends it as its own message, then checks that it
    /// really landed. The attach step can only report that the page <i>accepted</i> the
    /// events it was given, which is not the same as the file being sent — so the answer
    /// comes from the conversation itself: an image that was not there before.
    /// </summary>
    private async Task<string> AttachBannerAsync(Target chat, string path, CancellationToken ct)
    {
        var before = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.PanelState), ct).ConfigureAwait(false);
        var tried = new List<string>();

        // A genuine paste first. A chat that ignores synthesised events still honours this
        // one, because the browser itself reads the system clipboard and builds the event.
        if (await PasteBannerAsync(chat, path, ct).ConfigureAwait(false))
        {
            await Task.Delay(500, ct).ConfigureAwait(false);

            // Did the paste actually put a preview in the composer? Dispatching the key
            // event only means the browser was asked to paste. Without this check a paste
            // that did nothing still cost ten seconds of polling before the next route.
            if (await StagedAsync(chat, ct).ConfigureAwait(false) > 0)
            {
                await SendAttachmentAsync(chat, ct).ConfigureAwait(false);

                if (await BannerLandedAsync(chat, before, ct).ConfigureAwait(false))
                {
                    return "banner allegato (incollato dagli appunti)";
                }

                tried.Add("appunti: anteprima comparsa ma non inviata");
            }
            else
            {
                tried.Add("appunti: nessuna anteprima nella casella");
            }
        }
        else
        {
            tried.Add("appunti: incolla non riuscito");
        }

        // Only the script route needs the image inlined in the payload. Reading it is done
        // here and not at the top, because a banner too big to inline says nothing about
        // whether the clipboard route above could have carried it — and doing it first meant
        // one oversized file disabled the attachment entirely.
        var image = ReadImage(path, out var problem);
        if (image is null)
        {
            tried.Add($"script: {problem}");
            return $"banner NON allegato ({string.Join(" · ", tried)})";
        }

        var attached = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Attach, imageJson: image), ct)
            .ConfigureAwait(false);
        if (attached is null || !Flag(attached.Value, "ok"))
        {
            tried.Add($"script: {Reason(attached)}");
            return $"banner NON allegato ({string.Join(" · ", tried)})";
        }

        // Only long enough for the upload to start; WaitForSendReadyAsync does the waiting.
        await Task.Delay(300, ct).ConfigureAwait(false);
        await SendAttachmentAsync(chat, ct).ConfigureAwait(false);

        if (await BannerLandedAsync(chat, before, ct).ConfigureAwait(false))
        {
            return $"banner allegato ({Reason(attached)})";
        }

        tried.Add($"script: {Reason(attached)}, ma la chat non l'ha mostrato");

        // The image is still on the clipboard from the paste attempt, so the seller can drop
        // it into the chat by hand instead of hunting for the file.
        return $"banner NON allegato ({string.Join(" · ", tried)}) — è negli appunti, incollalo a mano";
    }

    /// <summary>How many attachment previews are sitting in the composer, waiting to be sent.</summary>
    private async Task<int> StagedAsync(Target chat, CancellationToken ct)
    {
        var staged = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Staged), ct).ConfigureAwait(false);
        return staged is null || !Flag(staged.Value, "ok") ? 0 : Number(staged.Value, "staged");
    }

    /// <summary>
    /// Sends what the upload left staged in the composer.
    /// </summary>
    /// <remarks>
    /// The button is the gesture that counts here, so it goes first: a composer holding only
    /// a file has no text, and most chats ignore Enter in that state. Enter stays as the
    /// fallback, and it is only reached when the preview is still sitting there — which also
    /// stops the two gestures from sending the same attachment twice.
    /// </remarks>
    /// <summary>How long the upload gets to finish before the send button is pressed anyway.</summary>
    private const int UploadWaitMs = 15_000;

    /// <summary>
    /// How many times the send button is pressed before giving up on it.
    /// </summary>
    /// <remarks>
    /// More than one because Eldorado's own upload dialog needs it: the first press is
    /// swallowed and the picture stays put, by hand exactly as from here. Pressing again is
    /// safe — the retry only happens while the attachment is demonstrably still sitting in
    /// the composer, so a press that did work is never repeated.
    /// </remarks>
    private const int SendAttempts = 3;

    private async Task SendAttachmentAsync(Target chat, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= SendAttempts; attempt++)
        {
            await WaitForSendReadyAsync(chat, ct).ConfigureAwait(false);

            var clicked = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.ClickSend), ct)
                .ConfigureAwait(false);

            // Logged whether it worked or not: when the chat markup moves, this line is the
            // difference between "the button was renamed" and "we pressed too early".
            ApiLog.Write($"[chat] invio allegato {attempt}/{SendAttempts}: {Reason(clicked)}");

            // The attachment leaving the composer is the only proof it went out — the click
            // reports that the button was pressed, not that the chat did anything with it.
            if (await WaitStagedClearedAsync(chat, 1_500, ct).ConfigureAwait(false))
            {
                if (attempt > 1)
                {
                    ApiLog.Write($"[chat] allegato partito al tentativo {attempt}");
                }

                return;
            }
        }

        // Still there after every press: Enter as the last resort.
        await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Submit), ct).ConfigureAwait(false);
        await Task.Delay(SendMs, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the composer to let go of the staged attachment. True when it did, which is
    /// what tells a press that worked from one the chat swallowed.
    /// </summary>
    private async Task<bool> WaitStagedClearedAsync(Target chat, int timeoutMs, CancellationToken ct)
    {
        var waited = 0;

        while (true)
        {
            if (await StagedAsync(chat, ct).ConfigureAwait(false) == 0)
            {
                return true;
            }

            if (waited >= timeoutMs)
            {
                return false;
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
            waited += 200;
        }
    }

    /// <summary>
    /// Waits for the chat to be willing to send. A file goes up in the background and the
    /// send button stays greyed out until it lands — pressing during that window does
    /// nothing at all, which is what made the bot look like it never pressed send.
    /// </summary>
    private async Task WaitForSendReadyAsync(Target chat, CancellationToken ct)
    {
        var waited = 0;
        while (waited < UploadWaitMs)
        {
            var state = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.SendState), ct).ConfigureAwait(false);
            if (state is null || !Flag(state.Value, "ok"))
            {
                return;   // nothing to read: let the click step report what it finds
            }

            if (Flag(state.Value, "ready"))
            {
                if (waited > 0)
                {
                    ApiLog.Write($"[chat] upload finito dopo {waited} ms · pulsante \"{Text(state.Value, "label")}\"");
                }

                return;
            }

            if (!Flag(state.Value, "waiting"))
            {
                // No send button at all, disabled or otherwise: waiting cannot help.
                ApiLog.Write("[chat] nessun pulsante di invio nella barra: si usa Invio");
                return;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
            waited += 250;
        }

        ApiLog.Write($"[chat] upload ancora in corso dopo {UploadWaitMs} ms: premo comunque");
    }

    /// <summary>
    /// Stages the banner on the Windows clipboard and has the browser perform a real paste
    /// into the composer, through the DevTools protocol's editing command. Unlike the
    /// synthesised paste this one is the browser's own, so a chat that checks the event was
    /// trusted still takes it. It needs no window focus, so it never steals the keyboard.
    /// </summary>
    private async Task<bool> PasteBannerAsync(Target chat, string path, CancellationToken ct)
    {
        // The paste lands wherever the caret is: put it in the composer first.
        var focused = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.FocusComposer), ct)
            .ConfigureAwait(false);
        if (focused is null || !Flag(focused.Value, "ok"))
        {
            return false;
        }

        if (!dispatcher.Invoke(() => StageOnClipboard(path)))
        {
            return false;
        }

        try
        {
            await dispatcher.InvokeAsync(() => browser.CoreWebView2!
                    .CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", PasteKeyEvent))
                .Task.Unwrap().ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            ApiLog.Write($"[chat] incolla reale non riuscito: {ex.Message}");
            return false;
        }
    }

    /// <summary>Ctrl+V carrying the editing command, which is what actually pastes.</summary>
    private const string PasteKeyEvent =
        """{"type":"keyDown","windowsVirtualKeyCode":86,"nativeVirtualKeyCode":86,"key":"v","code":"KeyV","modifiers":2,"commands":["paste"]}""";

    /// <summary>
    /// Puts the image on the clipboard both as a bitmap and as a file, because chats read
    /// one or the other. Runs on the UI thread: the clipboard is single-threaded apartment.
    /// </summary>
    private static bool StageOnClipboard(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // release the file handle
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                var data = new DataObject();
                data.SetImage(bitmap);
                data.SetFileDropList(new StringCollection { path });

                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                // Another process can hold the clipboard open for a moment.
                ApiLog.Write($"[chat] appunti occupati ({attempt + 1}/3): {ex.Message}");
                Thread.Sleep(150);
            }
        }

        return false;
    }

    /// <summary>Waits for the image to show up in the conversation; false when it never does.</summary>
    private async Task<bool> BannerLandedAsync(Target chat, JsonElement? before, CancellationToken ct)
    {
        if (before is null || !Flag(before.Value, "ok"))
        {
            return false;   // no baseline to compare against: never claim success
        }

        var images = Number(before.Value, "images");
        var links = Number(before.Value, "links");

        // Uploads are not instant: give the conversation a few seconds to show the file.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(400, ct).ConfigureAwait(false);

            var now = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.PanelState), ct).ConfigureAwait(false);
            if (now is null || !Flag(now.Value, "ok"))
            {
                continue;
            }

            if (Number(now.Value, "images") > images || Number(now.Value, "links") > links)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Dumps what every frame looks like to the selectors — the report to send along when
    /// the chat markup changed and delivery starts failing.
    /// </summary>
    public async Task<string> DiagnoseAsync(string? buyer, CancellationToken ct = default)
    {
        var script = ChatScripts.Compose(ChatScripts.Diagnose, buyer: buyer ?? "");
        var frames = new List<object>();

        foreach (var target in BuildTargets())
        {
            var data = await EvalAsync(target, script, ct).ConfigureAwait(false);
            frames.Add(new { frame = target.Label, data = data is null ? null : (object)data.Value });
        }

        var report = new
        {
            when = DateTimeOffset.Now,
            buyer,
            source = dispatcher.Invoke(() => browser.Source?.ToString() ?? ""),
            settings = config().ChatUrl,
            frames
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    // ----- page plumbing -------------------------------------------------------------

    /// <summary>One document a script can run in: the top page or one of its frames.</summary>
    private sealed record Target(string Label, Func<string, Task<string>> Run);

    private sealed record Probed(Target Target, JsonElement Data)
    {
        public bool HasComposer => Flag(Data, "hasComposer");
        public bool HasSearch => Flag(Data, "hasSearch");
        public bool HasRow => Number(Data, "matches") > 0;
    }

    private List<Target> BuildTargets() => dispatcher.Invoke(() =>
    {
        var targets = new List<Target>
        {
            new("documento", script => RunAsync(() => browser.CoreWebView2!.ExecuteScriptAsync(script)))
        };

        CoreWebView2Frame[] frames;
        lock (_frames)
        {
            frames = _frames.ToArray();
        }

        foreach (var frame in frames)
        {
            try
            {
                if (frame.IsDestroyed() != 0)
                {
                    continue;
                }

                var label = string.IsNullOrEmpty(frame.Name) ? "iframe" : $"iframe «{frame.Name}»";
                targets.Add(new Target(label, script => RunAsync(() => frame.ExecuteScriptAsync(script))));
            }
            catch (Exception ex)
            {
                ApiLog.Write($"[chat] frame non utilizzabile: {ex.Message}");
            }
        }

        return targets;
    });

    /// <summary>Runs one step in every frame and returns the first that reports success.</summary>
    private async Task<JsonElement?> RunEverywhereAsync(string script, CancellationToken ct)
    {
        foreach (var target in BuildTargets())
        {
            var result = await EvalAsync(target, script, ct).ConfigureAwait(false);
            if (result is { } value && Flag(value, "ok"))
            {
                return value;
            }
        }

        return null;
    }

    private async Task<List<Probed>> ProbeAsync(string buyer, CancellationToken ct)
    {
        var script = ChatScripts.Compose(ChatScripts.Probe, buyer: buyer);
        var probes = new List<Probed>();

        foreach (var target in BuildTargets())
        {
            var data = await EvalAsync(target, script, ct).ConfigureAwait(false);
            if (data is { } value && Flag(value, "ok"))
            {
                probes.Add(new Probed(target, value));
            }
        }

        return probes;
    }

    /// <summary>
    /// Polls until the composer the "chat with the buyer" button brings up has rendered.
    /// </summary>
    /// <remarks>
    /// Short and quick on purpose: the conversation is already on screen, so this only has
    /// to outlast the widget mounting. It replaces <see cref="WaitForInboxAsync"/> on this
    /// route, which waited for a list or a filter that a chat opened this way never has.
    /// </remarks>
    private async Task<List<Probed>> WaitForComposerAsync(string buyer, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 8_000;

        while (true)
        {
            var probes = await ProbeAsync(buyer, ct).ConfigureAwait(false);
            if (probes.Any(p => p.HasComposer) || Environment.TickCount64 >= deadline)
            {
                return probes;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Polls until the inbox has rendered — it is a SPA, the page load means little.</summary>
    private async Task<List<Probed>> WaitForInboxAsync(string buyer, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 15_000;
        List<Probed> probes;

        while (true)
        {
            probes = await ProbeAsync(buyer, ct).ConfigureAwait(false);
            if (probes.Any(p => p.HasRow) || probes.Any(p => p.HasComposer && p.HasSearch))
            {
                return probes;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return probes;
            }

            await Task.Delay(600, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Navigates and reports where the browser actually ended up after redirects.</summary>
    private async Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        var landed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Invoke(() =>
        {
            var core = browser.CoreWebView2!;

            void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= OnCompleted;
                landed.TrySetResult(e.IsSuccess);
            }

            core.NavigationCompleted += OnCompleted;

            try
            {
                core.Navigate(Absolute(url));
            }
            catch (Exception ex)
            {
                core.NavigationCompleted -= OnCompleted;
                ApiLog.Write($"[chat] navigazione a {url} fallita: {ex.Message}");
                landed.TrySetResult(false);
            }
        });

        await Task.WhenAny(landed.Task, Task.Delay(15_000, ct)).ConfigureAwait(false);

        // The site routes on its own after the document loads; read the address once settled.
        await Task.Delay(1500, ct).ConfigureAwait(false);
        return dispatcher.Invoke(() => browser.Source?.ToString() ?? "");
    }

    /// <summary>
    /// Did the address survive the navigation? A deleted request does not 404 — Eldorado
    /// redirects to a marketing page, which carries neither the chat nor its button, and
    /// whose footer does carry things that look like one.
    /// </summary>
    private static bool StillOnRequest(string landed, string requested)
    {
        try
        {
            var id = new Uri(Absolute(requested)).Segments.LastOrDefault()?.Trim('/');
            return string.IsNullOrEmpty(id) ||
                   landed.Contains(id, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return true;    // a template we cannot read: do not block on it
        }
    }

    private static string Absolute(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;

    /// <summary>True when the browser is already on the inbox (the site prefixes a locale).</summary>
    private static bool IsInbox(string current, string chatUrl)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        try
        {
            var path = new Uri(Absolute(chatUrl)).AbsolutePath.TrimEnd('/');
            return path.Length > 1 && current.Contains(path, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private string? DeepLink(OfferMessageSettings settings, OutgoingOfferMessage message)
    {
        var template = (settings.ConversationUrl ?? "").Trim();
        if (template.Length == 0)
        {
            return null;
        }

        return template
            .Replace("{buyer}", Uri.EscapeDataString(message.BuyerUsername ?? ""))
            .Replace("{buyerId}", Uri.EscapeDataString(message.BuyerId ?? ""))
            .Replace("{requestId}", Uri.EscapeDataString(message.RequestId));
    }

    private Task<string> RunAsync(Func<Task<string>> action) => dispatcher.InvokeAsync(action).Task.Unwrap();

    /// <summary>Runs one step and parses its <c>{ok, …}</c> contract; null when it blew up.</summary>
    private async Task<JsonElement?> EvalAsync(Target target, string script, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string raw;
        try
        {
            // A page that stops answering — a modal dialog, a wedged renderer — would leave
            // this await pending forever, and with it the whole bot loop, which waits for
            // the message before moving to the next request. Nothing here is worth a hang.
            var run = target.Run(script);
            if (await Task.WhenAny(run, Task.Delay(ScriptTimeoutMs, ct)).ConfigureAwait(false) != run)
            {
                ApiLog.Write($"[chat] {target.Label}: nessuna risposta entro {ScriptTimeoutMs} ms");
                return null;
            }

            raw = await run.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ApiLog.Write($"[chat] {target.Label}: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            ApiLog.Write($"[chat] {target.Label}: risposta non leggibile {raw}");
            return null;
        }
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>Addresses go in log lines and in the UI: keep them readable.</summary>
    private static string Shorten(string url) => url.Length <= 70 ? url : url[..67] + "…";

    private static string Reason(JsonElement? element) =>
        element is { } value ? Text(value, "reason") : "nessuna risposta dalla pagina";

    /// <summary>The image travels inside the injected script, so it has to stay small.</summary>
    private const int MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>Reads the banner as the <c>{name, type, data}</c> payload the attach step wants.</summary>
    /// <remarks>
    /// An oversized banner is re-encoded smaller rather than refused. Picking a 4 MB
    /// screenshot as your banner is an ordinary thing to do, and the old behaviour turned
    /// 160 KB over the line into "no banner at all" — reported as "4 MB, massimo 4", which
    /// reads like the file was exactly at the limit.
    /// </remarks>
    private static string? ReadImage(string path, out string problem)
    {
        problem = "";

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                problem = "file non trovato";
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var name = Path.GetFileName(path);
            var type = MimeType(path);

            if (bytes.Length > MaxImageBytes)
            {
                var smaller = Shrink(path, out var why);
                if (smaller is null)
                {
                    problem = $"immagine di {Megabytes(bytes.Length)} non riducibile sotto " +
                              $"{Megabytes(MaxImageBytes)} ({why})";
                    return null;
                }

                ApiLog.Write($"[chat] banner ridotto da {Megabytes(bytes.Length)} a {Megabytes(smaller.Length)}");
                bytes = smaller;
                name = Path.GetFileNameWithoutExtension(path) + ".jpg";
                type = "image/jpeg";
            }

            return JsonSerializer.Serialize(new
            {
                name,
                type,
                data = Convert.ToBase64String(bytes)
            });
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Re-encodes the banner as a JPEG, halving the width until it fits. Returns null when
    /// even the smallest step is still too big, or the file isn't a readable image.
    /// </summary>
    private static byte[]? Shrink(string path, out string problem)
    {
        problem = "";

        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;          // release the file handle
            source.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            source.UriSource = new Uri(path, UriKind.Absolute);
            source.EndInit();
            source.Freeze();

            foreach (var width in new[] { source.PixelWidth, 1600, 1200, 900, 640, 480 })
            {
                if (width > source.PixelWidth || width <= 0)
                {
                    continue;   // never upscale: it would only add bytes
                }

                BitmapSource frame = source;
                if (width < source.PixelWidth)
                {
                    var scale = (double)width / source.PixelWidth;
                    var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                    scaled.Freeze();
                    frame = scaled;
                }

                var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(BitmapFrame.Create(frame));

                using var buffer = new MemoryStream();
                encoder.Save(buffer);

                if (buffer.Length <= MaxImageBytes)
                {
                    return buffer.ToArray();
                }
            }

            problem = "resta troppo grande anche a 480 px";
            return null;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };
}
