namespace EldoradoApp.Services;

// Outgoing request bodies for the seller API. Property names are PascalCase here
// and serialized to camelCase via EldoradoApiJson.WriteOptions.

// ---- POST /api/boostingOffers ----

internal sealed class CreateBoostingOfferRequest
{
    public required BoostingOfferDetailsRequest Details { get; init; }
}

internal sealed class BoostingOfferDetailsRequest
{
    public required string BoostingRequestId { get; init; }

    /// <summary>GuaranteedDeliveryTime enum value as its string name (e.g. "Hour3", "Automated").</summary>
    public required string GuaranteedDeliveryTime { get; init; }

    public required OfferPricingRequest Pricing { get; init; }
}

internal sealed class OfferPricingRequest
{
    public int Quantity { get; init; }
    public int MinQuantity { get; init; }
    public required MoneyRequest PricePerUnit { get; init; }
    public List<VolumeDiscountRequest>? VolumeDiscounts { get; init; }
}

internal sealed class MoneyRequest
{
    public decimal Amount { get; init; }
    public required string Currency { get; init; }
}

internal sealed class VolumeDiscountRequest
{
    public int Quantity { get; init; }
    public int Percentage { get; init; }
}

// ---- POST /api/boostingOffers/me/boostingSubscription/create ----

internal sealed class CreateBoostingSubscriptionRequest
{
    public required BoostingIdRequest BoostingId { get; init; }
}

internal sealed class BoostingIdRequest
{
    public required string GameId { get; init; }
    public required string BoostingCategoryId { get; init; }
}
