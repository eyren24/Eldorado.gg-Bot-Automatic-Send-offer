using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>Computed offer for a parsed rank range.</summary>
public sealed record BoostQuote(int Divisions, decimal Price, double Hours, BoostingDeliveryTime DeliveryTime);

/// <summary>
/// Computes price and estimated delivery time for a boost from the per-division
/// price/hours tables in <see cref="BoostingBotSettings"/>. The delivery time is
/// snapped to the nearest <see cref="BoostingDeliveryTime"/> bucket, since the API
/// only accepts enum values rather than arbitrary durations.
/// </summary>
public static class BoostOfferCalculator
{
    public static BoostQuote? Quote(BoostingBotSettings settings, string? currentRank, string? desiredRank)
    {
        if (currentRank is null || desiredRank is null)
        {
            return null;
        }

        var start = ValorantRanks.DivisionIndex(currentRank);
        var end = ValorantRanks.DivisionIndex(desiredRank);
        if (end <= start)
        {
            return null;
        }

        var price = settings.FlatFee;
        var hours = 0d;
        for (var i = start; i < end; i++)
        {
            var tierEntered = ValorantRanks.Tier(ValorantRanks.Divisions[i + 1]);
            price += settings.PricePerDivision(tierEntered);
            hours += settings.HoursPerDivision(tierEntered);
        }

        return new BoostQuote(end - start, Math.Round(price, 2), hours, MapDeliveryTime(hours));
    }

    private static readonly (double MaxHours, BoostingDeliveryTime Time)[] Buckets =
    [
        (1, BoostingDeliveryTime.Hour1),
        (2, BoostingDeliveryTime.Hour2),
        (3, BoostingDeliveryTime.Hour3),
        (5, BoostingDeliveryTime.Hour5),
        (8, BoostingDeliveryTime.Hour8),
        (12, BoostingDeliveryTime.Hour12),
        (24, BoostingDeliveryTime.Day1),
        (48, BoostingDeliveryTime.Day2),
        (72, BoostingDeliveryTime.Day3),
        (168, BoostingDeliveryTime.Day7),
        (336, BoostingDeliveryTime.Day14),
        (672, BoostingDeliveryTime.Day28),
        (1080, BoostingDeliveryTime.Day45)
    ];

    public static BoostingDeliveryTime MapDeliveryTime(double hours)
    {
        foreach (var (max, time) in Buckets)
        {
            if (hours <= max)
            {
                return time;
            }
        }

        return BoostingDeliveryTime.Day60;
    }
}
