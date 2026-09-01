using CommunityToolkit.Mvvm.ComponentModel;
using EldoradoApp.Models;

namespace EldoradoApp.ViewModels;

/// <summary>
/// Editable row in the category list: whether the bot answers the category, how fast it
/// promises delivery, and — only for categories that have no rank range — a flat price
/// that replaces the ladder (0 = price by ladder).
/// </summary>
/// <remarks>
/// Writes straight through to the underlying <see cref="CategoryPricing"/> and reports the
/// edit upward, exactly like <see cref="DivisionPriceRow"/>.
/// <para>
/// It used to be a detached copy that only reached the settings when someone pressed
/// "Salva impostazioni" on this very page. Clearing a flat price here and then saving from
/// the Prezzi page wrote the <i>old</i> value straight back to disk, and the bot kept
/// bidding the flat price it was supposed to have stopped using — so the write-through is
/// the fix, not a refactor.
/// </para>
/// </remarks>
public sealed partial class CategoryPricingRow : ObservableObject
{
    private readonly CategoryPricing _model;
    private readonly Action _onChanged;

    /// <summary>Set while <see cref="FlatPrice"/> rewrites the cell text, so it isn't read back as an edit.</summary>
    private bool _formatting;

    /// <summary>The settings object this row edits in place.</summary>
    public CategoryPricing Model => _model;

    public string GameId => _model.GameId;
    public string CategoryId => _model.CategoryId;
    public string CategoryName => _model.CategoryName;

    /// <summary>
    /// The text in the flat-price cell — this is what the grid binds, so the price lands on
    /// the model at every keystroke instead of when the cell loses focus (see <see cref="MoneyText"/>).
    /// </summary>
    [ObservableProperty] private string _flatPriceInput;

    public CategoryPricingRow(CategoryPricing model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
        _flatPriceInput = MoneyText.Format(model.FlatPrice);
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set => Write(value, _model.Enabled, v => _model.Enabled = v);
    }

    public BoostingCategoryKind Kind
    {
        get => _model.Kind;
        set => Write(value, _model.Kind, v => _model.Kind = v);
    }

    public int Quantity
    {
        get => _model.Quantity;
        set => Write(value, _model.Quantity, v => _model.Quantity = v);
    }

    public int MinQuantity
    {
        get => _model.MinQuantity;
        set => Write(value, _model.MinQuantity, v => _model.MinQuantity = v);
    }

    public BoostingDeliveryTime DeliveryTime
    {
        get => _model.DeliveryTime;
        set => Write(value, _model.DeliveryTime, v => _model.DeliveryTime = v);
    }

    /// <summary>
    /// The flat price itself. Setting it rewrites the cell text; reading it always goes to
    /// the model, so it can't drift from what the bot will charge.
    /// </summary>
    public decimal FlatPrice
    {
        get => _model.FlatPrice;
        set
        {
            if (value == _model.FlatPrice && MoneyText.TryParse(FlatPriceInput, out var shown) && shown == value)
            {
                return;   // nothing moved: don't re-price the request feed
            }

            _model.FlatPrice = value;

            _formatting = true;
            try
            {
                FlatPriceInput = MoneyText.Format(value);
            }
            finally
            {
                _formatting = false;
            }

            OnPropertyChanged(nameof(FlatPrice));
            _onChanged();
        }
    }

    partial void OnFlatPriceInputChanged(string value)
    {
        // A half-typed cell keeps the last good price rather than blanking it to zero.
        if (_formatting || !MoneyText.TryParse(value, out var price) || price == _model.FlatPrice)
        {
            return;
        }

        _model.FlatPrice = price;
        OnPropertyChanged(nameof(FlatPrice));
        _onChanged();
    }

    /// <summary>Writes a value through to the model and reports the edit, if it actually moved.</summary>
    private void Write<T>(T value, T current, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        apply(value);
        OnPropertyChanged(property);
        _onChanged();
    }
}
