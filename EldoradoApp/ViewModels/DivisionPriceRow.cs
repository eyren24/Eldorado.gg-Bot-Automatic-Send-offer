using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EldoradoApp.Models;

namespace EldoradoApp.ViewModels;

/// <summary>
/// One editable rung of the rank ladder. Writes straight through to the underlying
/// <see cref="DivisionPrice"/> so the settings object is always current, and reports
/// the edit upward so the price simulator refreshes as you type.
/// </summary>
public sealed partial class DivisionPriceRow : ObservableObject
{
    private readonly DivisionPrice _model;
    private readonly Action _onChanged;

    public string Division => _model.Division;

    /// <summary>Tier name, used to group and colour the ladder.</summary>
    public string Tier => ValorantRanks.Tier(_model.Division);

    public Brush TierBrush { get; }

    /// <summary>Ladder position, shown as "#3" so a custom ladder is still readable.</summary>
    public int Step { get; }

    public DivisionPriceRow(DivisionPrice model, int step, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
        Step = step;
        _price = model.Price;
        TierBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ValorantRanks.ColorHex(model.Division))!);
        TierBrush.Freeze();
    }

    [ObservableProperty] private decimal _price;

    partial void OnPriceChanged(decimal value)
    {
        _model.Price = value;
        _onChanged();
    }
}
