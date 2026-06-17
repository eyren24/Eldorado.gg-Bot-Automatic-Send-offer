namespace EldoradoApp.Models;

/// <summary>
/// Guaranteed delivery time for a boosting offer. Names match the Eldorado
/// <c>GuaranteedDeliveryTime</c> enum exactly, so <see cref="object.ToString"/>
/// yields the value the API expects. <see cref="Automated"/> is the "automatic"
/// delivery time the seller panel computes.
/// </summary>
public enum BoostingDeliveryTime
{
    Automated,
    Instant,
    Minute5,
    Minute20,
    Hour1,
    Hour2,
    Hour3,
    Hour5,
    Hour8,
    Hour12,
    Day1,
    Day2,
    Day3,
    Day7,
    Day14,
    Day28,
    Day45,
    Day60,
    NotApplicable
}
