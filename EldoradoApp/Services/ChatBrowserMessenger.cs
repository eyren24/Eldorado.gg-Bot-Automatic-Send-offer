using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EldoradoApp.Services;

/// <summary>
/// Sends the post-offer message by driving the Eldorado chat inside the app's own
/// browser tab.
/// </summary>
/// <remarks>
/// Eldorado's seller API exposes no messaging endpoint — the site's chat is TalkJS,
/// rendered in a cross-origin iframe — so the only way to post as the seller is to act
/// on the logged-in web session. The script is injected into the top document and into
/// every child frame, and the first one that finds a message box wins. Because the chat
/// markup belongs to a third party, the script lives in
/// <see cref="Models.OfferMessageSettings.ChatScript"/> and is editable from the UI, and
/// any failure falls through to <see cref="ClipboardOfferMessenger"/>.
/// </remarks>
public sealed class ChatBrowserMessenger(WebView2 browser, Dispatcher dispatcher) : IOfferMessenger
{
    private readonly List<CoreWebView2Frame> _frames = [];
    private bool _hooked;

    /// <summary>Custom injection script from the settings; empty falls back to <see cref="DefaultScript"/>.</summary>
    public string? ScriptOverride { get; set; }

    public string Name => "Browser integrato";

    public bool IsReady => dispatcher.Invoke(() => browser.CoreWebView2 is not null);

    /// <summary>Starts tracking child frames so the script can reach the chat iframe.</summary>
    public void Attach()
    {
        if (_hooked || browser.CoreWebView2 is null)
        {
            return;
        }

        _hooked = true;
        browser.CoreWebView2.FrameCreated += (_, e) =>
        {
            _frames.Add(e.Frame);
            e.Frame.Destroyed += (s, _) =>
            {
                if (s is CoreWebView2Frame frame)
                {
                    _frames.Remove(frame);
                }
            };
        };
    }

    public async Task<OfferMessageResult> SendAsync(
        OutgoingOfferMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            return OfferMessageResult.Failed("browser integrato non inizializzato");
        }

        var script = BuildScript(message.Text);

        // Top document first — then every child frame (the chat widget lives in one).
        var attempts = new List<string>();

        var top = await RunAsync(() => browser.CoreWebView2!.ExecuteScriptAsync(script)).ConfigureAwait(false);
        if (Succeeded(top, out var detail))
        {
            return OfferMessageResult.Sent($"messaggio inviato in chat ({detail})");
        }

        attempts.Add($"documento: {detail}");

        var frames = dispatcher.Invoke(() => _frames.ToArray());
        foreach (var frame in frames)
        {
            string frameResult;
            try
            {
                frameResult = await RunAsync(() => frame.ExecuteScriptAsync(script)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                attempts.Add($"frame: {ex.Message}");
                continue;
            }

            if (Succeeded(frameResult, out var frameDetail))
            {
                return OfferMessageResult.Sent($"messaggio inviato in chat ({frameDetail})");
            }

            attempts.Add($"frame: {frameDetail}");
        }

        return OfferMessageResult.Failed(
            $"casella messaggi non trovata — apri la conversazione del compratore ({string.Join("; ", attempts.Take(3))})");
    }

    private Task<string> RunAsync(Func<Task<string>> action) => dispatcher.InvokeAsync(action).Task.Unwrap();

    /// <summary>ExecuteScript returns JSON; unwrap our <c>{ok, reason}</c> contract from it.</summary>
    private static bool Succeeded(string? json, out string detail)
    {
        detail = "nessuna risposta";
        if (string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // A thrown script comes back as a plain string.
            if (root.ValueKind != JsonValueKind.Object)
            {
                detail = root.ToString();
                return false;
            }

            detail = root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "";
            return root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            detail = json;
            return false;
        }
    }

    private string BuildScript(string text)
    {
        var template = string.IsNullOrWhiteSpace(ScriptOverride) ? DefaultScript : ScriptOverride!;
        return template.Replace("__TEXT__", JsonSerializer.Serialize(text));
    }

    /// <summary>
    /// Types into the chat's message box and submits it. Kept deliberately generic —
    /// it looks for the last visible editable element, which is what every chat widget
    /// (TalkJS included) uses for its composer.
    /// </summary>
    public const string DefaultScript = """
        (function (text) {
          try {
            var selectors = [
              'div[contenteditable="true"]',
              'textarea[placeholder]',
              'textarea',
              'input[type="text"][placeholder]'
            ];
            var box = null;
            for (var i = 0; i < selectors.length && !box; i++) {
              var visible = Array.prototype.slice
                .call(document.querySelectorAll(selectors[i]))
                .filter(function (e) { return e.offsetParent !== null && !e.disabled && !e.readOnly; });
              if (visible.length) { box = visible[visible.length - 1]; }
            }
            if (!box) { return { ok: false, reason: 'casella messaggi non trovata' }; }

            box.focus();
            if (box.isContentEditable) {
              box.textContent = text;
              box.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertText' }));
            } else {
              var proto = box.tagName === 'TEXTAREA'
                ? window.HTMLTextAreaElement.prototype
                : window.HTMLInputElement.prototype;
              var setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
              setter.call(box, text);
              box.dispatchEvent(new Event('input', { bubbles: true }));
            }

            var enter = function (type) {
              box.dispatchEvent(new KeyboardEvent(type, {
                key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true
              }));
            };
            enter('keydown'); enter('keypress'); enter('keyup');

            var form = box.closest ? box.closest('form') : null;
            if (form) {
              var submit = form.querySelector('button[type="submit"], input[type="submit"]');
              if (submit) { submit.click(); }
              else { form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); }
            }

            return { ok: true, reason: box.tagName.toLowerCase() };
          } catch (e) {
            return { ok: false, reason: String(e) };
          }
        })(__TEXT__);
        """;
}
