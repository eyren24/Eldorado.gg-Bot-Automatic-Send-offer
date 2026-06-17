using CommunityToolkit.Mvvm.ComponentModel;

namespace EldoradoApp.ViewModels;

/// <summary>Editable per-tier row in the bot config: price + hours per division, and an exclude flag.</summary>
public sealed partial class TierConfigRow : ObservableObject
{
    public string Tier { get; }

    [ObservableProperty] private decimal _pricePerDivision;
    [ObservableProperty] private double _hoursPerDivision;
    [ObservableProperty] private bool _excluded;

    public TierConfigRow(string tier, decimal pricePerDivision, double hoursPerDivision, bool excluded)
    {
        Tier = tier;
        _pricePerDivision = pricePerDivision;
        _hoursPerDivision = hoursPerDivision;
        _excluded = excluded;
    }
}
