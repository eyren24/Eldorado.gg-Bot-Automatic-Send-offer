using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EldoradoApp.Models;

namespace EldoradoApp.ViewModels;

/// <summary>
/// One tier's price per game/win (placements and net wins). Writes straight through to
/// the model so the settings object is always current, and reports the edit upward so
/// the simulator refreshes as you type.
/// </summary>
public sealed partial class TierPriceRow : ObservableObject
{
    private readonly TierUnitPrice _model;
    private readonly Action _onChanged;

    public string Tier => _model.Tier;

    public Brush TierBrush { get; }

    [ObservableProperty] private decimal _pricePerUnit;

    public TierPriceRow(TierUnitPrice model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
        _pricePerUnit = model.PricePerUnit;
        TierBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ValorantRanks.ColorHex(model.Tier))!);
        TierBrush.Freeze();
    }

    partial void OnPricePerUnitChanged(decimal value)
    {
        _model.PricePerUnit = value;
        _onChanged();
    }
}

/// <summary>One region's accept switch and price multiplier, writing through to the model.</summary>
public sealed partial class RegionRow : ObservableObject
{
    private readonly RegionRule _model;
    private readonly Action _onChanged;

    public string Code => _model.Code;
    public string Name => _model.Name;

    [ObservableProperty] private bool _accepted;
    [ObservableProperty] private decimal _multiplier;

    public RegionRow(RegionRule model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
        _accepted = model.Accepted;
        _multiplier = model.Multiplier;
    }

    partial void OnAcceptedChanged(bool value)
    {
        _model.Accepted = value;
        _onChanged();
    }

    partial void OnMultiplierChanged(decimal value)
    {
        _model.Multiplier = value <= 0 ? 1m : value;
        _onChanged();
    }
}
