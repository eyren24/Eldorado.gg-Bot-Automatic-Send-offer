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

    /// <summary>Set while <see cref="Price"/> rewrites the cell text, so it isn't read back as an edit.</summary>
    private bool _formatting;

    public string Division => _model.Division;

    /// <summary>Tier name, used to group and colour the ladder.</summary>
    public string Tier => ValorantRanks.Tier(_model.Division);

    public Brush TierBrush { get; }

    /// <summary>Ladder position, shown as "#3" so a custom ladder is still readable.</summary>
    public int Step { get; }

    /// <summary>
    /// The text in the price cell — this is what the grid binds, so the price lands on the
    /// model at every keystroke instead of when the cell loses focus (see <see cref="MoneyText"/>).
    /// </summary>
    [ObservableProperty] private string _priceInput;

    public DivisionPriceRow(DivisionPrice model, int step, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
        Step = step;
        _priceInput = MoneyText.Format(model.Price);
        TierBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ValorantRanks.ColorHex(model.Division))!);
        TierBrush.Freeze();
    }

    /// <summary>
    /// The price itself. Setting it (bulk fill, ±10%, suggested list) rewrites the cell text;
    /// reading it always goes to the model, so it can't drift from what the bot will charge.
    /// </summary>
    public decimal Price
    {
        get => _model.Price;
        set
        {
            if (value == _model.Price && MoneyText.TryParse(PriceInput, out var shown) && shown == value)
            {
                return;   // nothing moved: don't re-run the simulator and the request feed
            }

            _model.Price = value;

            _formatting = true;
            try
            {
                PriceInput = MoneyText.Format(value);
            }
            finally
            {
                _formatting = false;
            }

            OnPropertyChanged(nameof(Price));
            _onChanged();
        }
    }

    partial void OnPriceInputChanged(string value)
    {
        // A half-typed cell keeps the last good price rather than blanking it to zero.
        if (_formatting || !MoneyText.TryParse(value, out var price) || price == _model.Price)
        {
            return;
        }

        _model.Price = price;
        OnPropertyChanged(nameof(Price));
        _onChanged();
    }
}
