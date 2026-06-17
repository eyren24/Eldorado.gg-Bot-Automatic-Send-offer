using System.Net.Http;
using System.Net.Http.Json;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Real <see cref="IBoostingRequestClient"/> backed by
/// <c>GET /api/boostingOffers/me/boostingRequests/received</c>.
/// Requires an authenticated <see cref="HttpClient"/> (see <see cref="EldoradoAuthHandler"/>).
/// </summary>
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

        var payload = await response.Content
            .ReadFromJsonAsync<CursorPagedResult<BoostingRequestDto>>(EldoradoApiJson.Options, cancellationToken)
            .ConfigureAwait(false);

        var dtos = payload?.Results ?? [];
        return dtos.Select(Map).ToList();
    }

    private static BoostingRequest Map(BoostingRequestDto dto) => new(
        Id: dto.Id,
        GameId: dto.GameId,
        BoostingCategoryId: dto.BoostingCategoryId,
        BoostingCategoryTitle: dto.BoostingCategoryTitle,
        BuyerId: dto.BuyerId,
        BuyerUsername: dto.BuyerUsername,
        IsBuyerMuted: dto.IsBuyerMuted,
        CreatedDate: dto.CreatedDate);
}
