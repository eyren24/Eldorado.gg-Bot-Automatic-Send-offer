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
/// The feed is thin — id, buyer, category label, nothing about the job itself — so every
/// page is handed to a <see cref="BoostingRequestHydrator"/>, which fetches each request's
/// form answers (current rank, desired rank, server, game count) and attaches them as
/// <see cref="BoostingRequest.Facts"/>. Without that step nothing downstream can price a
/// request, because the rank range simply is not in this response.
/// </remarks>
public sealed class EldoradoBoostingRequestClient(HttpClient http) : IBoostingRequestClient
{
    private readonly BoostingRequestHydrator _hydrator = new(http);

    /// <summary>Form schemas and fetched details, exposed for the request inspector.</summary>
    public BoostingRequestHydrator Hydrator => _hydrator;

    public async Task<IReadOnlyList<BoostingRequest>> GetReceivedRequestsAsync(
        BoostingRequestFilter filter = BoostingRequestFilter.ActiveRequests,
        string? gameId = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default,
        bool hydrate = true)
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

        if (!hydrate)
        {
            return list;
        }

        // The feed carries no rank range; the form answers do.
        var hydrated = await _hydrator.HydrateAsync(list, cancellationToken).ConfigureAwait(false);

        if (_hydrator.FailedCount > 0)
        {
            ApiLog.Write($"[hydrate] {_hydrator.FailedCount}/{hydrated.Count} richieste senza dettagli leggibili");
        }

        return hydrated;
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
