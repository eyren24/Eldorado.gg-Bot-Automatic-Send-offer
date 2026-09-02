using System.IO;

namespace EldoradoApp.Models;

/// <summary>How the follow-up chat message is delivered after an offer goes out.</summary>
public enum MessageDelivery
{
    /// <summary>Drive the chat inside the app's embedded browser (fully automatic).</summary>
    AutoBrowser,

    /// <summary>Only stage the message + banner on the clipboard and notify.</summary>
    ClipboardOnly,

    /// <summary>Drive a dedicated persistent Edge profile through Playwright.</summary>
    PlaywrightBrowser
}

/// <summary>
/// The message the bot fires the moment an offer is submitted, plus the banner image
/// that goes with it. The template understands placeholders — see
/// <c>OfferMessageComposer.Placeholders</c>.
/// </summary>
public sealed class OfferMessageSettings
{
    /// <summary>Master switch for the post-offer message.</summary>
    public bool Enabled { get; set; } = true;

    public MessageDelivery Delivery { get; set; } = MessageDelivery.PlaywrightBrowser;

    /// <summary>Message body with {placeholders}.</summary>
    public string Template { get; set; } =
        "Ciao {buyer} 👋\n" +
        "Ho appena inviato la mia offerta per {from} → {to} ({divisions} divisioni).\n" +
        "💰 Prezzo: {price}\n" +
        "⏱️ Consegna stimata: {eta}\n" +
        "{extras}\n" +
        "Sono un booster affidabile: niente cheat, account sempre al sicuro, aggiornamenti costanti.\n" +
        "Accetta l'offerta e partiamo subito! 🚀";

    /// <summary>
    /// Send the template as several chat messages — one per block separated by a blank
    /// line — instead of one long one.
    /// </summary>
    /// <remarks>
    /// On by default because it is how a person writes in a chat, and because a chat
    /// composer sends on Enter: a single message can only hold soft line breaks, and a
    /// widget that refuses those would otherwise flatten the whole template onto one line.
    /// </remarks>
    public bool SplitMessages { get; set; } = true;

    /// <summary>
    /// Pause between two consecutive chat messages. Short: they are meant to land in quick
    /// succession, and this only has to outlast the composer clearing itself.
    /// </summary>
    public int BetweenMessagesMs { get; set; } = 400;

    /// <summary>Absolute path of the banner image attached to the message (optional).</summary>
    public string BannerPath { get; set; } = "";

    /// <summary>Copy text (and banner) to the clipboard as well, as a manual fallback.</summary>
    public bool CopyToClipboard { get; set; } = true;

    /// <summary>Also attach the banner inside the chat, not only on the clipboard.</summary>
    public bool AttachBanner { get; set; } = true;

    /// <summary>
    /// Refuse to write when the open conversation cannot be confirmed as the buyer's.
    /// Off by default: the bot already refuses when the buyer's row isn't there at all, or
    /// when the open chat is provably somebody else's, and some skins give no further
    /// signal to check against.
    /// </summary>
    public bool StrictBuyerMatch { get; set; }

    /// <summary>
    /// Page the bot opens to reach the buyer: <c>{requestId}</c>, <c>{buyer}</c> and
    /// <c>{buyerId}</c> are substituted.
    /// </summary>
    /// <remarks>
    /// This is the route that actually works right after an offer. The conversation does
    /// not exist yet at that moment — it is the request page's own "chat with the buyer"
    /// button that creates it — so there is nothing to find in the message list, and
    /// hunting for the buyer's name there just spins until somebody opens the chat by hand.
    /// </remarks>
    public string ConversationUrl { get; set; } = DefaultConversationUrl;

    /// <summary>The page of a single boosting request, which carries the chat button.</summary>
    public const string DefaultConversationUrl = "https://www.eldorado.gg/boosting-request/{requestId}";

    /// <summary>Wait this long after the offer before sending, to let the chat thread appear.</summary>
    public int DelaySeconds { get; set; } = 2;

    /// <summary>Page the embedded browser opens to reach the buyer conversation.</summary>
    public string ChatUrl { get; set; } = DefaultChatUrl;

    /// <summary>The seller's message inbox on Eldorado.</summary>
    public const string DefaultChatUrl = "https://www.eldorado.gg/dashboard/messages";

    /// <summary>How many times a failed delivery is retried before it's parked as manual.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Advanced: the JavaScript that types into Eldorado's chat box. Empty = the built-in
    /// script. Exposed because the chat widget is third-party markup that can change; the
    /// snippet must evaluate to <c>{ ok: bool, reason: string }</c> and gets the message
    /// text substituted for the <c>__TEXT__</c> token.
    /// </summary>
    public string ChatScript { get; set; } = "";

    /// <summary>Selectors and profile options for the reliable Playwright delivery channel.</summary>
    public PlaywrightMessageOptions Playwright { get; set; } = new();

    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerPath) && File.Exists(BannerPath);
}

/// <summary>
/// The defaults intentionally use semantic, broad selectors. Eldorado's chat is third-party
/// markup, so each selector can be adjusted without recompiling if their UI changes.
/// </summary>
public sealed class PlaywrightMessageOptions
{
    public bool Enabled { get; set; } = true;
    public bool Headless { get; set; }
    public string BrowserChannel { get; set; } = "msedge";
    public string ProfilePath { get; set; } = "";
    public int ActionTimeoutMs { get; set; } = 15_000;
    public int VerificationTimeoutMs { get; set; } = 12_000;
    public string ComposerSelector { get; set; } = "textarea, [contenteditable='true'], [role='textbox']";
    public string SendButtonSelector { get; set; } = "button, [role='button'], [type='submit']";
    public string FileInputSelector { get; set; } = "input[type='file']";
    public string AttachButtonSelector { get; set; } = "button, [role='button'], label";

    public void Normalize()
    {
        BrowserChannel = string.IsNullOrWhiteSpace(BrowserChannel) ? "msedge" : BrowserChannel.Trim();
        ActionTimeoutMs = Math.Clamp(ActionTimeoutMs, 3_000, 60_000);
        VerificationTimeoutMs = Math.Clamp(VerificationTimeoutMs, 3_000, 60_000);
        ComposerSelector = string.IsNullOrWhiteSpace(ComposerSelector)
            ? "textarea, [contenteditable='true'], [role='textbox']" : ComposerSelector.Trim();
        SendButtonSelector = string.IsNullOrWhiteSpace(SendButtonSelector)
            ? "button, [role='button'], [type='submit']" : SendButtonSelector.Trim();
        FileInputSelector = string.IsNullOrWhiteSpace(FileInputSelector)
            ? "input[type='file']" : FileInputSelector.Trim();
        AttachButtonSelector = string.IsNullOrWhiteSpace(AttachButtonSelector)
            ? "button, [role='button'], label" : AttachButtonSelector.Trim();
    }
}
