using System.IO;
using System.Text.Json;
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
    private const int SettleMs = 900;

    /// <summary>Time the chat gets to accept a send gesture before it is checked.</summary>
    private const int SendMs = 700;

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
            return await DeliverAsync(message, buyer, config(), cancellationToken).ConfigureAwait(false);
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

        var written = await WriteAsync(chat, message.Text, settings, ct).ConfigureAwait(false);
        if (written is { } textFailure)
        {
            return textFailure;
        }

        if (settings.AttachBanner && message.HasBanner)
        {
            notes.Add(await AttachBannerAsync(chat, message.BannerPath!, ct).ConfigureAwait(false));
        }

        var detail = notes.Count == 0 ? "" : " · " + string.Join(" · ", notes);
        return OfferMessageResult.Sent($"inviato a {buyer}{detail}");
    }

    /// <summary>Brings the buyer's conversation on screen and proves it is really theirs.</summary>
    private async Task<(Target? Chat, OfferMessageResult? Failure)> OpenConversationAsync(
        OutgoingOfferMessage message, string buyer, OfferMessageSettings settings, List<string> notes,
        CancellationToken ct)
    {
        // A direct conversation link, when the seller has one, beats clicking around.
        var link = DeepLink(settings, message);
        if (link is not null)
        {
            await NavigateAsync(link, ct).ConfigureAwait(false);
            notes.Add("link diretto");
        }
        else if (!IsInbox(dispatcher.Invoke(() => browser.Source?.ToString() ?? ""), settings.ChatUrl))
        {
            await NavigateAsync(settings.ChatUrl, ct).ConfigureAwait(false);
        }

        var probes = await WaitForInboxAsync(buyer, ct).ConfigureAwait(false);
        if (probes.Count == 0)
        {
            return (null, OfferMessageResult.Failed(
                "la pagina della chat non risponde: apri la scheda Chat e accedi a Eldorado"));
        }

        if (link is null)
        {
            var failure = await SelectRowAsync(buyer, probes, notes, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return (null, failure);
            }
        }

        // Opening a conversation rebuilds the composer, and can move it to another frame.
        probes = await ProbeAsync(buyer, ct).ConfigureAwait(false);
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
        await Task.Delay(200, ct).ConfigureAwait(false);
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

    /// <summary>Puts the banner in the composer and sends it as its own message.</summary>
    private async Task<string> AttachBannerAsync(Target chat, string path, CancellationToken ct)
    {
        var image = ReadImage(path, out var problem);
        if (image is null)
        {
            return $"banner non allegato ({problem})";
        }

        var attached = await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Attach, imageJson: image), ct)
            .ConfigureAwait(false);
        if (attached is null || !Flag(attached.Value, "ok"))
        {
            return $"banner non allegato ({Reason(attached)})";
        }

        // The upload needs a moment before the chat will accept the send gesture.
        await Task.Delay(1500, ct).ConfigureAwait(false);
        await EvalAsync(chat, ChatScripts.Compose(ChatScripts.Submit), ct).ConfigureAwait(false);
        await Task.Delay(SendMs, ct).ConfigureAwait(false);
        await EvalAsync(chat, ChatScripts.Compose(ChatScripts.ClickSend), ct).ConfigureAwait(false);

        return $"banner: {Reason(attached)}";
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

    private async Task NavigateAsync(string url, CancellationToken ct)
    {
        dispatcher.Invoke(() =>
        {
            try
            {
                browser.CoreWebView2!.Navigate(Absolute(url));
            }
            catch (Exception ex)
            {
                ApiLog.Write($"[chat] navigazione a {url} fallita: {ex.Message}");
            }
        });

        await Task.Delay(1500, ct).ConfigureAwait(false);
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
            raw = await target.Run(script).ConfigureAwait(false);
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

    private static string Reason(JsonElement? element) =>
        element is { } value ? Text(value, "reason") : "nessuna risposta dalla pagina";

    /// <summary>Reads the banner as the <c>{name, type, data}</c> payload the attach step wants.</summary>
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

            // The image travels inside the injected script, so it has to stay small.
            if (file.Length > 4 * 1024 * 1024)
            {
                problem = $"immagine troppo grande ({file.Length / 1024 / 1024} MB, massimo 4)";
                return null;
            }

            return JsonSerializer.Serialize(new
            {
                name = Path.GetFileName(path),
                type = MimeType(path),
                data = Convert.ToBase64String(File.ReadAllBytes(path))
            });
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };
}
