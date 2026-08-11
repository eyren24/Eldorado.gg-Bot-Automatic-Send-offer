namespace EldoradoApp.Models;

/// <summary>
/// One server region: whether the bot answers its requests, and how much the price
/// changes for it. Boosting outside the home region costs more (night shifts, higher
/// ping, fewer boosters), so each region carries its own multiplier.
/// </summary>
public sealed class RegionRule
{
    public string Code { get; set; } = "";

    /// <summary>Human-readable name shown next to the code.</summary>
    public string Name { get; set; } = "";

    /// <summary>Answer requests from this region.</summary>
    public bool Accepted { get; set; }

    /// <summary>Price factor for this region — 1 = no change, 1.25 = +25%.</summary>
    public decimal Multiplier { get; set; } = 1m;

    /// <summary>The regions the parser can recognise, with EU as the accepted default.</summary>
    public static List<RegionRule> CreateDefaults() =>
    [
        new() { Code = "EU", Name = "Europa", Accepted = true, Multiplier = 1.00m },
        new() { Code = "NA", Name = "Nord America", Accepted = true, Multiplier = 1.25m },
        new() { Code = "LATAM", Name = "America Latina", Accepted = false, Multiplier = 1.30m },
        new() { Code = "BR", Name = "Brasile", Accepted = false, Multiplier = 1.30m },
        new() { Code = "AP", Name = "Asia Pacifico", Accepted = false, Multiplier = 1.40m },
        new() { Code = "KR", Name = "Corea", Accepted = false, Multiplier = 1.40m },
        new() { Code = "OCE", Name = "Oceania", Accepted = false, Multiplier = 1.40m },
        new() { Code = "TR", Name = "Turchia", Accepted = false, Multiplier = 1.20m },
        new() { Code = "MENA", Name = "Medio Oriente", Accepted = false, Multiplier = 1.30m },
    ];
}
