using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>A ready-to-send chat message produced right after an offer was submitted.</summary>
public sealed record OutgoingOfferMessage(
    string RequestId,
    string? BuyerId,
    string? BuyerUsername,
    string Text,
    string? BannerPath,
    DateTimeOffset CreatedAt)
{
    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerPath);
}

public enum MessageOutcome
{
    /// <summary>Delivered into the buyer conversation.</summary>
    Sent,

    /// <summary>Staged on the clipboard for the seller to paste (fallback).</summary>
    Staged,

    /// <summary>The browser may have sent it, but the page never gave reliable proof. Never retry automatically.</summary>
    Unknown,

    Failed,
    Disabled
}

/// <param name="Permanent">
/// True when retrying cannot possibly help — the request was deleted, the buyer is gone.
/// Without it every such message costs three attempts with their waits, and the bot loop,
/// which waits for the message before moving on, stalls for a minute per dead request.
/// </param>
public sealed record OfferMessageResult(
    MessageOutcome Outcome,
    string Detail,
    bool Permanent = false,
    bool Retryable = false)
{
    public static OfferMessageResult Sent(string detail) => new(MessageOutcome.Sent, detail);
    public static OfferMessageResult Staged(string detail) => new(MessageOutcome.Staged, detail);
    public static OfferMessageResult Failed(string detail) => new(MessageOutcome.Failed, detail);
    public static OfferMessageResult RetryableFailure(string detail) => new(MessageOutcome.Failed, detail, Retryable: true);
    public static OfferMessageResult Unknown(string detail) => new(MessageOutcome.Unknown, detail);

    /// <summary>A failure there is no point retrying.</summary>
    public static OfferMessageResult Gone(string detail) => new(MessageOutcome.Failed, detail, Permanent: true);
}

/// <summary>
/// Delivers the post-offer message. Eldorado's seller API has no chat endpoint (the
/// chat is TalkJS, embedded in the site), so delivery is done by driving the logged-in
/// web session inside the app's own browser; the clipboard implementation is the
/// always-available fallback.
/// </summary>
public interface IOfferMessenger
{
    /// <summary>False when this channel can't deliver right now (browser not signed in, …).</summary>
    bool IsReady { get; }

    /// <summary>Short name shown in the activity log.</summary>
    string Name { get; }

    Task<OfferMessageResult> SendAsync(OutgoingOfferMessage message, CancellationToken cancellationToken = default);
}
