using System.Net.Http;
using System.Net.Http.Json;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Real <see cref="IBoostingOfferClient"/>: submits boosting offers and manages
/// subscriptions. Requires an authenticated <see cref="HttpClient"/>.
/// </summary>
public sealed class EldoradoBoostingOfferClient(HttpClient http) : IBoostingOfferClient
{
    private const string OffersPath = "api/boostingOffers";
    private const string SubscriptionsPath = "api/boostingOffers/me/boostingSubscriptions";
    private const string SubscriptionCreatePath = "api/boostingOffers/me/boostingSubscription/create";

    public async Task SubmitOfferAsync(BoostingOfferDraft draft, CancellationToken cancellationToken = default)
    {
        var body = new CreateBoostingOfferRequest
        {
            Details = new BoostingOfferDetailsRequest
            {
                BoostingRequestId = draft.BoostingRequestId,
                GuaranteedDeliveryTime = draft.DeliveryTime.ToString(),
                Pricing = new OfferPricingRequest
                {
                    Quantity = draft.Quantity,
                    MinQuantity = draft.MinQuantity,
                    PricePerUnit = new MoneyRequest { Amount = draft.PricePerUnit, Currency = draft.Currency },
                    VolumeDiscounts = draft.VolumeDiscounts?
                        .Select(v => new VolumeDiscountRequest { Quantity = v.Quantity, Percentage = v.Percentage })
                        .ToList()
                }
            }
        };

        using var response = await http
            .PostAsJsonAsync(OffersPath, body, EldoradoApiJson.WriteOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BoostingSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(SubscriptionsPath, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var dtos = await response.Content
            .ReadFromJsonAsync<List<BoostingSubscriptionDto>>(EldoradoApiJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? [];

        return dtos.Select(d => new BoostingSubscription(
            d.GameId, d.GameName, d.GameCategoryTitle,
            d.BoostingCategoryId, d.BoostingCategoryName, d.IsActive, d.IsSubscribed)).ToList();
    }

    public async Task SubscribeAsync(string gameId, string boostingCategoryId, CancellationToken cancellationToken = default)
    {
        var body = new CreateBoostingSubscriptionRequest
        {
            BoostingId = new BoostingIdRequest { GameId = gameId, BoostingCategoryId = boostingCategoryId }
        };

        using var response = await http
            .PostAsJsonAsync(SubscriptionCreatePath, body, EldoradoApiJson.WriteOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(string gameId, string boostingCategoryId, CancellationToken cancellationToken = default)
    {
        var path = $"api/boostingOffers/me/boostingSubscription/{Uri.EscapeDataString(gameId)}/{Uri.EscapeDataString(boostingCategoryId)}";
        using var response = await http.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Surface the server's problem detail to make failed offers diagnosable.
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(detail, 500)}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";
}
