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

        // The frame this handler belongs to is captured directly rather than read off the
        // sender: when the sender came back as something else the entry was never removed,
        // and the list filled up with handles to frames of pages navigated away from. Every
        // later step then talked to a dead iframe and got "cannot be accessed after the
        // WebView2 control is disposed" — including the one carrying the banner.
        frame.Destroyed += (_, _) => Forget(frame);

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

    /// <summary>
    /// The conversation's frame, looked up again when the one in hand has died.
    /// </summary>
    /// <remarks>
    /// Pressing the request page's chat button makes TalkJS tear its iframe down and build a
    /// new one, so the frame that answered the probe a moment ago can already be gone by the
    /// time the banner is uploaded. A captured handle then fails for the rest of the
    /// delivery — every step, hundreds of milliseconds apart, against a page that no longer
    /// exists. Dropping it from the tracked list does not help: the target still holds it.
    /// </remarks>
    private async Task<Target> LiveChatAsync(Target current, string buyer, CancellationToken ct)
    {
        // A trivial script is the cheapest way to ask "are you still there?".
        if (await EvalAsync(current, ChatScripts.Compose(ChatScripts.Pending), ct).ConfigureAwait(false) is not null)
        {
            return current;
        }

        ApiLog.Write($"[chat] {current.Label} non risponde piu': cerco di nuovo la conversazione");

        var probes = await WaitForComposerAsync(buyer, 4_000, ct).ConfigureAwait(false);
        var fresh = probes.FirstOrDefault(p => p.HasComposer)?.Target;
        if (fresh is null)
        {
            return current;   // nothing better to offer; the caller reports what it finds
        }

        ApiLog.Write($"[chat] conversazione ritrovata in {fresh.Label}");
        return fresh;
    }

    /// <summary>Drops a frame from the tracked list; safe to call more than once.</summary>
    private void Forget(CoreWebView2Frame frame)
    {
        lock (_frames)
        {
            _frames.Remove(frame);
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

            // Deliberately no "park the browser back on the inbox" here. Every delivery opens
            // by navigating to its own request page, so the parking bought nothing — and it
            // is what the seller could see happening: a failed offer sent the browser to the
            // messages dashboard, and the next one dragged it back to a request. Two page
            // loads per cycle, purely to end up where the next step was going anyway.
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

        // The banner leads: it is what makes the buyer stop scrolling, so it goes above the
        // pitch, and sending it while the composer is still empty keeps the upload from
        // racing a half-typed message.
        if (settings.AttachBanner && message.HasBanner)
        {
            // The chat button remounts the widget, so the frame picked a moment ago may
            // already be gone. Check before every stage rather than once at the start.
            chat = await LiveChatAsync(chat, buyer, ct).ConfigureAwait(false);

            // Deliberately no retry on a fresh frame. It was tried: it doubled the work -
            // another shrink, another upload, another eight seconds and another visible
            // remount - and never once recovered a banner, because in that state the widget
            // keeps tearing itself down. The cure is upstream, in not provoking the remount
            // at all; see EmbeddedChatWaitMs.
            var banner = await SendBannerAsync(chat, message.BannerPath!, ct).ConfigureAwait(false);
            notes.Add(banner.Note);
        }

        // Then the text, back to back.
        chat = await LiveChatAsync(chat, buyer, ct).ConfigureAwait(false);

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0 && settings.BetweenMessagesMs > 0)
            {
                // Just enough for the chat to clear its composer before the next one is
                // typed in; the messages are meant to land one after another, not drip.
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

            // The request page already carries the conversation — the "Live chat with buyer"
            // panel further down — its iframe just needs a moment to mount. Waiting for it
            // is what removes the detour the seller can watch happening: press the button,
            // land on the messages dashboard, come back to the request. Probing once, right
            // after the load, was always too early to see it.
            probes = await WaitForComposerAsync(buyer, EmbeddedChatWaitMs, ct).ConfigureAwait(false);
            if (probes.Any(p => p.HasComposer))
            {
                notes.Add("chat gia' nella pagina");
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
                // Let the widget finish tearing itself down first. Probing immediately after
                // the press catches the frame on its way out, and that dying handle is the
                // one the whole delivery then tries to talk to.
                await Task.Delay(SettleMs, ct).ConfigureAwait(false);

                probes = await WaitForComposerAsync(buyer, 8_000, ct).ConfigureAwait(false);
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

    // ---- The banner ----

    /// <summary>
    /// Puts the banner in the conversation: upload it, wait for the upload to finish, press
    /// send. Returns the line that goes in the activity log.
    /// </summary>
    /// <remarks>
    /// Three plain steps, because every earlier shape of this hid the failure somewhere. The
    /// only thing trusted as proof is the conversation itself: an image that was not above
    /// the composer before and is now. A click reports that a button was pressed; the
    /// picture arriving is what says it worked.
    /// </remarks>
    private async Task<(bool Sent, string Note)> SendBannerAsync(Target chat, string path, CancellationToken ct)
    {
        var before = await ConversationImagesAsync(chat, ct).ConfigureAwait(false);

        // 1 - UPLOAD.
        var how = await UploadBannerAsync(chat, path, ct).ConfigureAwait(false);
        if (how is null)
        {
            await DumpFailureAsync(ct).ConfigureAwait(false);
            return (false, "banner NON caricato: la chat non ha accettato ne' il campo file ne' gli appunti");
        }

        // 2 - WAIT FOR THE PICTURE, not for a button.
        //
        // TalkJS uploads and posts the file by itself, with no send press at all: its send
        // button belongs to the text field and stays grey throughout, because there is no
        // text. Waiting on that button meant six dead seconds and then declaring a banner
        // lost that the buyer had already received.
        if (await WaitImageAppearedAsync(chat, before, BannerWaitMs, ct).ConfigureAwait(false))
        {
            return (true, $"banner inviato ({how})");
        }

        // 3 - PRESS SEND. Only for a chat that does not post the file on its own, and where
        // the first press can be swallowed by its upload dialog.
        for (var attempt = 1; attempt <= SendAttempts; attempt++)
        {
            var clicked = await ClickSendAsync(chat, ct).ConfigureAwait(false);
            ApiLog.Write($"[chat] banner, invio {attempt}/{SendAttempts}: {Reason(clicked)}");

            if (await WaitImageAppearedAsync(chat, before, 1_500, ct).ConfigureAwait(false))
            {
                return (true, $"banner inviato ({how}, {attempt} pressioni)");
            }
        }

        await DumpFailureAsync(ct).ConfigureAwait(false);
        return (false, $"banner caricato ({how}) ma NON comparso in chat");
    }

    /// <summary>Gets the banner into the composer. Returns how it got there, or null.</summary>
    /// <remarks>
    /// The file input leads: it is the route that demonstrably reaches TalkJS, and it needs
    /// no window focus. Whether it worked is not decided here — the caller waits for the
    /// picture to appear in the conversation, which is the only signal that means anything.
    /// </remarks>
    private async Task<string?> UploadBannerAsync(Target chat, string path, CancellationToken ct)
    {
        var image = ReadImage(path, out var problem);
        if (image is null)
        {
            ApiLog.Write($"[chat] immagine non leggibile: {problem}");
        }
        else
        {
            var attached = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Attach, imageJson: image), ct)
                .ConfigureAwait(false);
            if (attached is { } value && Flag(value, "ok"))
            {
                return Reason(attached);
            }

            ApiLog.Write($"[chat] campo file: {Reason(attached)}");
        }

        // The browser's own paste, for a chat that has no file input to hand bytes to.
        if (await PasteBannerAsync(chat, path, ct).ConfigureAwait(false))
        {
            return "incollato dagli appunti";
        }

        return null;
    }

    /// <summary>Presses send, in the conversation's own frame and nowhere else.</summary>
    /// <remarks>
    /// Deliberately not tried across every frame. Hunting a send-looking control through the
    /// whole page is how a press landed on the upload dialog's Cancel and threw the picture
    /// away: outside the conversation there is nothing that can legitimately send this
    /// message, so there is nothing to look for there.
    /// </remarks>
    private async Task<JsonElement?> ClickSendAsync(Target chat, CancellationToken ct) =>
        await EvalAsync(chat, ChatScripts.Compose(ChatScripts.ClickSend), ct).ConfigureAwait(false);

    /// <summary>How many images the conversation shows above the composer; -1 when unreadable.</summary>
    private async Task<int> ConversationImagesAsync(Target chat, CancellationToken ct)
    {
        var state = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.PanelState), ct).ConfigureAwait(false);
        return state is null || !Flag(state.Value, "ok") ? -1 : Number(state.Value, "images");
    }

    /// <summary>
    /// Waits for one more image in the conversation than there was before - the only honest
    /// proof the banner was sent, since the send step can report a pressed button but never a
    /// delivered file.
    /// </summary>
    private async Task<bool> WaitImageAppearedAsync(
        Target chat, int before, int timeoutMs, CancellationToken ct)
    {
        if (before < 0)
        {
            return false;   // no baseline to compare against: never claim success
        }

        var waited = 0;
        while (waited < timeoutMs)
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            waited += 300;

            if (await ConversationImagesAsync(chat, ct).ConfigureAwait(false) > before)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How long the picture gets to appear in the conversation after the upload. Short,
    /// because this wait sits in front of the text messages.
    /// </summary>
    private const int BannerWaitMs = 6_000;

    /// <summary>
    /// How many times send is pressed when the chat did not post the file on its own.
    /// </summary>
    /// <remarks>
    /// One. TalkJS uploads and posts the picture by itself, so this press is a fallback for a
    /// chat that does not — and pressing repeatedly is what sometimes threw the picture away
    /// instead of sending it. A single press either helps or does nothing; a volley can undo
    /// an upload that had already worked.
    /// </remarks>
    private const int SendAttempts = 1;

    /// <summary>
    /// Writes what every frame looked like when the banner refused to go out, next to the
    /// settings as <c>chat-allegato-fallito.json</c>. Overwritten each time: it is the state
    /// of the last failure that matters, and this must never grow without bound.
    /// </summary>
    private async Task DumpFailureAsync(CancellationToken ct)
    {
        try
        {
            var report = await DiagnoseAsync(null, ct).ConfigureAwait(false);
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EldoradoApp", "chat-allegato-fallito.json");

            await File.WriteAllTextAsync(path, report, ct).ConfigureAwait(false);
            ApiLog.Write($"[chat] stato della pagina salvato in {path}");
        }
        catch (Exception ex)
        {
            ApiLog.Write($"[chat] diagnostica dell'allegato non riuscita: {ex.Message}");
        }
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

    /// <summary>
    /// One document a script can run in: the top page or one of its frames.
    /// </summary>
    /// <param name="Frame">
    /// The iframe this runs in, or null for the top page. Carried so a handle that turns out
    /// to be dead can be dropped from the tracked list the moment it throws.
    /// </param>
    private sealed record Target(string Label, Func<string, Task<string>> Run, CoreWebView2Frame? Frame = null);

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
                targets.Add(new Target(label, script => RunAsync(() => frame.ExecuteScriptAsync(script)), frame));
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
    /// How long the request page's own chat panel gets to mount before falling back to the
    /// button that scrolls down to it.
    /// </summary>
    /// <remarks>
    /// Generous on purpose, and this is the single most important number here. Every
    /// delivery that found the panel already on the page succeeded; every delivery that had
    /// to press the button failed, because the press makes TalkJS tear its iframe down and
    /// build a new one, and the banner upload then races a widget that is remounting under
    /// it. Waiting is free - a press costs the whole attachment. The button stays only for a
    /// request whose conversation genuinely does not exist yet.
    /// </remarks>
    private const int EmbeddedChatWaitMs = 12_000;

    /// <summary>
    /// Polls until the conversation's composer has rendered.
    /// </summary>
    /// <remarks>
    /// Short and quick on purpose: the conversation is on the page already, so this only has
    /// to outlast the widget mounting. It replaces <see cref="WaitForInboxAsync"/> on this
    /// route, which waited for a list or a filter that a chat opened this way never has.
    /// </remarks>
    private async Task<List<Probed>> WaitForComposerAsync(
        string buyer, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

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

            // A frame that throws is gone for good: drop it so the next step does not spend
            // another round trip talking to a page that no longer exists.
            if (target.Frame is { } dead)
            {
                Forget(dead);
            }

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
