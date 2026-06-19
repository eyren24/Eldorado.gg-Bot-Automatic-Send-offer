using CommunityToolkit.Mvvm.ComponentModel;
using EldoradoApp.Models;

namespace EldoradoApp.ViewModels;

/// <summary>Editable row in the per-category price list: price/unit, quantity range, delivery time, on/off.</summary>
public sealed partial class CategoryPricingRow : ObservableObject
{
    public string GameId { get; }
    public string CategoryId { get; }
    public string CategoryName { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private decimal _pricePerUnit;
    [ObservableProperty] private int _quantity;
    [ObservableProperty] private int _minQuantity;
    [ObservableProperty] private BoostingDeliveryTime _deliveryTime;

    public CategoryPricingRow(CategoryPricing source)
    {
        GameId = source.GameId;
        CategoryId = source.CategoryId;
        CategoryName = source.CategoryName;
        _enabled = source.Enabled;
        _pricePerUnit = source.PricePerUnit;
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
        PricePerUnit = PricePerUnit,
        Quantity = Quantity,
        MinQuantity = MinQuantity,
        DeliveryTime = DeliveryTime,
    };
}
