using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>
/// The price list screen: a flat base price plus one editable surcharge per division of
/// the ladder, with bulk-fill helpers and a live simulator that shows exactly which
/// divisions a given range bills and what the buyer would be quoted.
/// </summary>
public sealed partial class PricingViewModel : ObservableObject
{
    private readonly SettingsHost _host;
    private bool _loading;

    private BoostingBotSettings Settings => _host.Settings;
    private RankPricing Pricing => Settings.Pricing;

    public ObservableCollection<DivisionPriceRow> Divisions { get; } = [];

    /// <summary>Ladder entries, for the simulator's two dropdowns.</summary>
    public ObservableCollection<string> Ladder { get; } = [];

    /// <summary>Tier names for the bulk-fill dropdown ("tutti" first).</summary>
    public ObservableCollection<string> Tiers { get; } = [];

    /// <summary>Per-region accept switch + price multiplier.</summary>
    public ObservableCollection<RegionRow> Regions { get; } = [];

    /// <summary>Region codes for the simulator ("—" = region not detected).</summary>
    public ObservableCollection<string> RegionCodes { get; } = [];

    [ObservableProperty] private bool _acceptAllRegions;
    [ObservableProperty] private bool _acceptUnknownRegion;
    [ObservableProperty] private string _simulationRegion = "—";

    public Array PriceModes { get; } = Enum.GetValues(typeof(OfferPriceMode));
    public Array DeliveryTimes { get; } = Enum.GetValues(typeof(BoostingDeliveryTime));

    // ---- Global knobs ----
    [ObservableProperty] private decimal _basePrice;
    [ObservableProperty] private string _currency = "EUR";
    [ObservableProperty] private bool _includeStartDivision;
    [ObservableProperty] private decimal _minimumPrice;
    [ObservableProperty] private decimal _roundTo;
    [ObservableProperty] private decimal _multiplier = 1m;
    [ObservableProperty] private OfferPriceMode _priceMode;
    [ObservableProperty] private BoostingDeliveryTime _defaultDeliveryTime;
    [ObservableProperty] private bool _skipUnparsableRanges;

    // ---- Bulk fill ----
    [ObservableProperty] private string _bulkTier = "Tutti";
    [ObservableProperty] private decimal _bulkAmount;

    // ---- Simulator ----
    [ObservableProperty] private string? _simulationFrom;
    [ObservableProperty] private string? _simulationTo;
    [ObservableProperty] private string _simulationTotal = "—";
    [ObservableProperty] private string _simulationRange = "Scegli due rank per vedere il prezzo";
    public ObservableCollection<PriceLine> SimulationLines { get; } = [];

    public PricingViewModel(SettingsHost host)
    {
        _host = host;
        Reload();
    }

    /// <summary>Rebuilds every row and control from the current settings object.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            Settings.Normalized();

            BasePrice = Pricing.BasePrice;
            Currency = Settings.Currency;
            IncludeStartDivision = Pricing.IncludeStartDivision;
            MinimumPrice = Pricing.MinimumPrice;
            RoundTo = Pricing.RoundTo;
            Multiplier = Pricing.Multiplier;
            PriceMode = Pricing.PriceMode;
            DefaultDeliveryTime = Settings.DefaultDeliveryTime;
            SkipUnparsableRanges = Settings.SkipUnparsableRanges;

            Divisions.Clear();
            for (var i = 0; i < Pricing.Divisions.Count; i++)
            {
                Divisions.Add(new DivisionPriceRow(Pricing.Divisions[i], i + 1, OnRowEdited));
            }

            Ladder.Clear();
            foreach (var division in Pricing.Ladder.Divisions)
            {
                Ladder.Add(division);
            }

            Tiers.Clear();
            Tiers.Add("Tutti");
            foreach (var tier in Pricing.Ladder.Divisions.Select(ValorantRanks.Tier).Distinct())
            {
                Tiers.Add(tier);
            }

            AcceptAllRegions = Settings.AcceptAllRegions;
            AcceptUnknownRegion = Settings.AcceptUnknownRegion;

            Regions.Clear();
            RegionCodes.Clear();
            RegionCodes.Add("—");
            foreach (var region in Settings.Regions)
            {
                Regions.Add(new RegionRow(region, OnRowEdited));
                RegionCodes.Add(region.Code);
            }

            SimulationFrom ??= Ladder.FirstOrDefault(d => d.StartsWith("Gold", StringComparison.OrdinalIgnoreCase));
            SimulationTo ??= Ladder.FirstOrDefault(d => d.StartsWith("Platinum", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _loading = false;
        }

        Simulate();
    }

    private void OnRowEdited()
    {
        if (!_loading)
        {
            Simulate();
            _host.Touch();
        }
    }

    /// <summary>Copies the editable fields back into the settings object.</summary>
    public void Apply()
    {
        Pricing.BasePrice = BasePrice;
        Pricing.IncludeStartDivision = IncludeStartDivision;
        Pricing.MinimumPrice = MinimumPrice;
        Pricing.RoundTo = RoundTo;
        Pricing.Multiplier = Multiplier <= 0 ? 1m : Multiplier;
        Pricing.PriceMode = PriceMode;
        Settings.Currency = string.IsNullOrWhiteSpace(Currency) ? "EUR" : Currency.Trim().ToUpperInvariant();
        Settings.DefaultDeliveryTime = DefaultDeliveryTime;
        Settings.SkipUnparsableRanges = SkipUnparsableRanges;
        Settings.AcceptAllRegions = AcceptAllRegions;
        Settings.AcceptUnknownRegion = AcceptUnknownRegion;
    }

    // Any knob change is applied immediately so the simulator never lies about the price.
    partial void OnBasePriceChanged(decimal value) => ApplyAndSimulate();
    partial void OnIncludeStartDivisionChanged(bool value) => ApplyAndSimulate();
    partial void OnMinimumPriceChanged(decimal value) => ApplyAndSimulate();
    partial void OnRoundToChanged(decimal value) => ApplyAndSimulate();
    partial void OnMultiplierChanged(decimal value) => ApplyAndSimulate();
    partial void OnPriceModeChanged(OfferPriceMode value) => ApplyAndSimulate();
    partial void OnCurrencyChanged(string value) => ApplyAndSimulate();
    partial void OnDefaultDeliveryTimeChanged(BoostingDeliveryTime value) => ApplyAndSimulate();
    partial void OnSkipUnparsableRangesChanged(bool value) => ApplyAndSimulate();
    partial void OnSimulationFromChanged(string? value) => Simulate();
    partial void OnSimulationToChanged(string? value) => Simulate();
    partial void OnSimulationRegionChanged(string value) => Simulate();
    partial void OnAcceptAllRegionsChanged(bool value) => ApplyAndSimulate();
    partial void OnAcceptUnknownRegionChanged(bool value) => ApplyAndSimulate();

    private void ApplyAndSimulate()
    {
        if (_loading)
        {
            return;
        }

        Apply();
        Simulate();
        _host.Touch();
    }

    /// <summary>Recomputes the example quote shown next to the price list.</summary>
    public void Simulate()
    {
        SimulationLines.Clear();

        if (SimulationFrom is null || SimulationTo is null)
        {
            SimulationTotal = "—";
            SimulationRange = "Scegli due rank per vedere il prezzo";
            return;
        }

        var region = SimulationRegion == "—" ? null : SimulationRegion;
        var quote = BoostingPriceCalculator.Quote(SimulationFrom, SimulationTo, [], Settings, region);

        foreach (var line in quote.Lines)
        {
            SimulationLines.Add(line);
        }

        SimulationTotal = quote.IsPriceable ? quote.TotalText : "—";
        SimulationRange = quote.IsPriceable
            ? $"{quote.RangeText} · {quote.DivisionCount} divisioni conteggiate"
            : quote.Problem ?? "non quotabile";
    }

    [RelayCommand]
    private void ApplyBulk()
    {
        foreach (var row in Divisions)
        {
            if (BulkTier == "Tutti" || string.Equals(row.Tier, BulkTier, StringComparison.OrdinalIgnoreCase))
            {
                row.Price = BulkAmount;
            }
        }

        Simulate();
        _host.Touch();
    }

    [RelayCommand]
    private void ScaleAll(string factorText)
    {
        if (!decimal.TryParse(factorText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var factor) || factor <= 0)
        {
            return;
        }

        foreach (var row in Divisions)
        {
            row.Price = Math.Round(row.Price * factor, 2, MidpointRounding.AwayFromZero);
        }

        Simulate();
        _host.Touch();
    }

    [RelayCommand]
    private void ResetToSuggested()
    {
        var suggested = RankPricing.CreateDefault();
        foreach (var row in Divisions)
        {
            row.Price = suggested.PriceOf(row.Division);
        }

        Simulate();
        _host.Touch();
    }

    [RelayCommand]
    private void Save()
    {
        Apply();
        _host.Save();
    }
}
