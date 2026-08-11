using CommunityToolkit.Mvvm.ComponentModel;
using EldoradoApp.Models;

namespace EldoradoApp.ViewModels;

/// <summary>
/// Editable row in the category list: whether the bot answers the category, how fast it
/// promises delivery, and — only for categories that have no rank range — a flat price
/// that replaces the ladder (0 = price by ladder).
/// </summary>
public sealed partial class CategoryPricingRow : ObservableObject
{
    public string GameId { get; }
    public string CategoryId { get; }
    public string CategoryName { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private BoostingCategoryKind _kind;
    [ObservableProperty] private decimal _flatPrice;
    [ObservableProperty] private int _quantity;
    [ObservableProperty] private int _minQuantity;
    [ObservableProperty] private BoostingDeliveryTime _deliveryTime;

    public CategoryPricingRow(CategoryPricing source)
    {
        GameId = source.GameId;
        CategoryId = source.CategoryId;
        CategoryName = source.CategoryName;
        _enabled = source.Enabled;
        _kind = source.Kind;
        _flatPrice = source.FlatPrice;
        _quantity = source.Quantity;
        _minQuantity = source.MinQuantity;
        _deliveryTime = source.DeliveryTime;
    }

    public CategoryPricing ToModel() => new()
    {
        GameId = GameId,
        CategoryId = CategoryId,
        CategoryName = CategoryName,
        Enabled = Enabled,
        Kind = Kind,
        FlatPrice = FlatPrice,
        Quantity = Quantity,
        MinQuantity = MinQuantity,
        DeliveryTime = DeliveryTime,
    };
}
