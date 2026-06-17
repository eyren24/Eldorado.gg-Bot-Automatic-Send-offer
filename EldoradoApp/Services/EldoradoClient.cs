using System.Net.Http;
using System.Net.Http.Json;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Real <see cref="IEldoradoClient"/> backed by the Eldorado Seller API. Reads the
/// seller's orders from <c>GET /api/v1/orders/me/seller/orders</c>.
/// Requires an authenticated <see cref="HttpClient"/> (see <see cref="EldoradoAuthHandler"/>).
/// </summary>
public sealed class EldoradoClient(HttpClient http) : IEldoradoClient
{
    // displayFilter is required by the endpoint; we want orders we are selling.
    private const string OrdersPath =
        "api/v1/orders/me/seller/orders?displayFilter=DisplaySellingOrders&pageSize=50";

    public async Task<IReadOnlyList<Order>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(OrdersPath, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync<CursorPagedResult<OrderDto>>(EldoradoApiJson.Options, cancellationToken)
            .ConfigureAwait(false);

        var dtos = payload?.Results ?? [];
        return dtos.Select(Map).ToList();
    }

    private static Order Map(OrderDto dto) => new(
        Id: dto.Id,
        BuyerUsername: dto.BuyerUsername ?? "—",
        ProductTitle: dto.OrderOfferDetails?.OfferTitle
                      ?? dto.OrderOfferDetails?.GameCategoryTitle
                      ?? "(senza titolo)",
        Amount: dto.TotalPrice?.Amount ?? 0m,
        Currency: dto.TotalPrice?.Currency ?? "USD",
        Status: MapStatus(dto.State?.State),
        CreatedAt: dto.CreatedDate);

    private static OrderStatus MapStatus(string? state) => state switch
    {
        null => OrderStatus.Pending,
        "Completed" => OrderStatus.Completed,
        "Canceled" => OrderStatus.Cancelled,
        var s when s.Contains("Disputed", StringComparison.OrdinalIgnoreCase) => OrderStatus.Disputed,
        "Delivered" or "Received" or "PendingReview" => OrderStatus.Processing,
        "Paid" or "Initialized" => OrderStatus.Pending,
        _ => OrderStatus.Pending
    };
}
