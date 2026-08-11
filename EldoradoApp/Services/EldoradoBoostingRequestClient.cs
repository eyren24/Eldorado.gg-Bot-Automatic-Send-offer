using System.Net.Http;
using System.Text.Json;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Real <see cref="IBoostingRequestClient"/> backed by
/// <c>GET /api/boostingOffers/me/boostingRequests/received</c>.
/// Requires an authenticated <see cref="HttpClient"/> (see <see cref="EldoradoAuthHandler"/>).
/// </summary>
/// <remarks>
/// Each item keeps its original JSON: the documented DTO only carries the category
/// title, but live payloads may also describe the rank range and the buyer's options,
/// which is exactly what the pricing engine wants to read.
/// </remarks>
public sealed class EldoradoBoostingRequestClient(HttpClient http) : IBoostingRequestClient
{
    public async Task<IReadOnlyList<BoostingRequest>> GetReceivedRequestsAsync(
        BoostingRequestFilter filter = BoostingRequestFilter.ActiveRequests,
        string? gameId = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/boostingOffers/me/boostingRequests/received?filter={filter}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(gameId))
        {
            path += $"&gameId={Uri.EscapeDataString(gameId)}";
        }

        using var response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<BoostingRequest>(results.GetArrayLength());
        foreach (var element in results.EnumerateArray())
        {
            var dto = element.Deserialize<BoostingRequestDto>(EldoradoApiJson.Options);
            if (dto is not null)
            {
                list.Add(Map(dto, element.GetRawText()));
            }
        }

        return list;
    }

    private static BoostingRequest Map(BoostingRequestDto dto, string rawJson) => new(
        Id: dto.Id,
        GameId: dto.GameId,
        BoostingCategoryId: dto.BoostingCategoryId,
        BoostingCategoryTitle: dto.BoostingCategoryTitle,
        BuyerId: dto.BuyerId,
        BuyerUsername: dto.BuyerUsername,
        IsBuyerMuted: dto.IsBuyerMuted,
        CreatedDate: dto.CreatedDate,
        RawJson: rawJson);
}
