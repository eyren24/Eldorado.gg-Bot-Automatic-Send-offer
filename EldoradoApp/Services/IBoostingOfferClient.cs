using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>What to offer in response to a boosting request.</summary>
public sealed record BoostingOfferDraft(
    string BoostingRequestId,
    BoostingDeliveryTime DeliveryTime,
    decimal PricePerUnit,
    string Currency,
    int Quantity,
    int MinQuantity,
    IReadOnlyList<VolumeDiscount>? VolumeDiscounts = null);

/// <summary>
/// Write operations against the seller boosting API: submitting offers and
/// managing category subscriptions (which control what requests are received).
/// </summary>
public interface IBoostingOfferClient
{
    /// <summary>Submits a boosting offer (<c>POST /api/boostingOffers</c>).</summary>
    Task SubmitOfferAsync(BoostingOfferDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Lists the seller's boosting category subscriptions.</summary>
    Task<IReadOnlyList<BoostingSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default);

    Task SubscribeAsync(string gameId, string boostingCategoryId, CancellationToken cancellationToken = default);

    Task UnsubscribeAsync(string gameId, string boostingCategoryId, CancellationToken cancellationToken = default);
}
