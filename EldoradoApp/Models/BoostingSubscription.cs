namespace EldoradoApp.Models;

/// <summary>
/// A boosting category the seller can subscribe to (so its requests are received).
/// On Eldorado a category is specific to a region + rank range, so subscriptions
/// are the server-side switch for enabling/disabling which jobs arrive.
/// </summary>
public sealed record BoostingSubscription(
    string? GameId,
    string? GameName,
    string? GameCategoryTitle,
    string? BoostingCategoryId,
    string? BoostingCategoryName,
    bool IsActive,
    bool IsSubscribed);
