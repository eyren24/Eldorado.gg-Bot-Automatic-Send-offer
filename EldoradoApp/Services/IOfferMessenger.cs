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

    Failed,
    Disabled
}

public sealed record OfferMessageResult(MessageOutcome Outcome, string Detail)
{
    public static OfferMessageResult Sent(string detail) => new(MessageOutcome.Sent, detail);
    public static OfferMessageResult Staged(string detail) => new(MessageOutcome.Staged, detail);
    public static OfferMessageResult Failed(string detail) => new(MessageOutcome.Failed, detail);
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
