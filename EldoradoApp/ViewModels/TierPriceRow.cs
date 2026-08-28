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

    /// <summary>Set while <see cref="PricePerUnit"/> rewrites the cell text, so it isn't read back as an edit.</summary>
    private bool _formatting;

    public string Tier => _model.Tier;

    public Brush TierBrush { get; }

    /// <summary>
    /// The text in the price cell — this is what the grid binds, so the price lands on the
    /// model at every keystroke instead of when the cell loses focus (see <see cref="MoneyText"/>).
    /// </summary>
    [ObservableProperty] private string _pricePerUnitInput;

    public TierPriceRow(TierUnitPrice model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
        _pricePerUnitInput = MoneyText.Format(model.PricePerUnit);
        TierBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ValorantRanks.ColorHex(model.Tier))!);
        TierBrush.Freeze();
    }

    /// <summary>The price itself; setting it rewrites the cell text, reading it goes to the model.</summary>
    public decimal PricePerUnit
    {
        get => _model.PricePerUnit;
        set
        {
            if (value == _model.PricePerUnit &&
                MoneyText.TryParse(PricePerUnitInput, out var shown) && shown == value)
            {
                return;   // nothing moved: don't re-run the simulator and the request feed
            }

            _model.PricePerUnit = value;

            _formatting = true;
            try
            {
                PricePerUnitInput = MoneyText.Format(value);
            }
            finally
            {
                _formatting = false;
            }

            OnPropertyChanged(nameof(PricePerUnit));
            _onChanged();
        }
    }

    partial void OnPricePerUnitInputChanged(string value)
    {
        // A half-typed cell keeps the last good price rather than blanking it to zero.
        if (_formatting || !MoneyText.TryParse(value, out var price) || price == _model.PricePerUnit)
        {
            return;
        }

        _model.PricePerUnit = price;
        OnPropertyChanged(nameof(PricePerUnit));
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
