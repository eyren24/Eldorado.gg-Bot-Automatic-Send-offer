using System.IO;

namespace EldoradoApp.Models;

/// <summary>How the follow-up chat message is delivered after an offer goes out.</summary>
public enum MessageDelivery
{
    /// <summary>Drive the chat inside the app's embedded browser (fully automatic).</summary>
    AutoBrowser,

    /// <summary>Only stage the message + banner on the clipboard and notify.</summary>
    ClipboardOnly
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

    public MessageDelivery Delivery { get; set; } = MessageDelivery.AutoBrowser;

    /// <summary>Message body with {placeholders}.</summary>
    public string Template { get; set; } =
        "Ciao {buyer} 👋\n" +
        "Ho appena inviato la mia offerta per {from} → {to} ({divisions} divisioni).\n" +
        "💰 Prezzo: {price}\n" +
        "⏱️ Consegna stimata: {eta}\n" +
        "{extras}\n" +
        "Sono un booster affidabile: niente cheat, account sempre al sicuro, aggiornamenti costanti.\n" +
        "Accetta l'offerta e partiamo subito! 🚀";

    /// <summary>Absolute path of the banner image attached to the message (optional).</summary>
    public string BannerPath { get; set; } = "";

    /// <summary>Copy text (and banner) to the clipboard as well, as a manual fallback.</summary>
    public bool CopyToClipboard { get; set; } = true;

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

    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerPath) && File.Exists(BannerPath);
}
