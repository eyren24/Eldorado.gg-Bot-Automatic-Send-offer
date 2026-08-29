using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>
/// Pricing for the per-game categories: placement matches (priced on last season's rank)
/// and net wins (priced on the current rank). Both work the same way — a base price plus
/// a per-game multiplier for every tier — and both have a live simulator.
/// </summary>
public sealed partial class UnitsViewModel : ObservableObject
{
    private readonly SettingsHost _host;
    private bool _loading;

    private BoostingBotSettings Settings => _host.Settings;

    public ObservableCollection<TierPriceRow> PlacementTiers { get; } = [];
    public ObservableCollection<TierPriceRow> NetWinTiers { get; } = [];

    /// <summary>Tier names for the two simulators.</summary>
    public ObservableCollection<string> Tiers { get; } = [];

    // ---- Placements ----
    [ObservableProperty] private bool _placementsEnabled;
    [ObservableProperty] private decimal _placementsBase;
    [ObservableProperty] private int _placementsDefaultQuantity;
    [ObservableProperty] private int _placementsMaxQuantity;
    [ObservableProperty] private string? _placementsSimTier;
    [ObservableProperty] private int _placementsSimQuantity = 5;
    [ObservableProperty] private string _placementsSimTotal = "—";
    [ObservableProperty] private string _placementsSimDetail = "";

    // ---- Net wins ----
    [ObservableProperty] private bool _netWinsEnabled;
    [ObservableProperty] private decimal _netWinsBase;
    [ObservableProperty] private int _netWinsDefaultQuantity;
    [ObservableProperty] private int _netWinsMaxQuantity;
    [ObservableProperty] private string? _netWinsSimTier;
    [ObservableProperty] private int _netWinsSimQuantity = 10;
    [ObservableProperty] private string _netWinsSimTotal = "—";
    [ObservableProperty] private string _netWinsSimDetail = "";

    public UnitsViewModel(SettingsHost host)
    {
        _host = host;
        Reload();
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            Settings.Normalized();

            var placements = Settings.Placements;
            PlacementsEnabled = placements.Enabled;
            PlacementsBase = placements.BasePrice;
            PlacementsDefaultQuantity = placements.DefaultQuantity;
            PlacementsMaxQuantity = placements.MaxQuantity;

            var netWins = Settings.NetWins;
            NetWinsEnabled = netWins.Enabled;
            NetWinsBase = netWins.BasePrice;
            NetWinsDefaultQuantity = netWins.DefaultQuantity;
            NetWinsMaxQuantity = netWins.MaxQuantity;

            PlacementTiers.Clear();
            foreach (var tier in placements.Tiers)
            {
                PlacementTiers.Add(new TierPriceRow(tier, OnEdited));
            }

            NetWinTiers.Clear();
            foreach (var tier in netWins.Tiers)
            {
                NetWinTiers.Add(new TierPriceRow(tier, OnEdited));
            }

            // Includes Unranked: it's the usual starting point for placements, so the
            // simulator has to be able to pick it.
            Tiers.Clear();
            foreach (var tier in ValorantRanks.UnitTiers)
            {
                Tiers.Add(tier);
            }

            PlacementsSimTier ??= "Gold";
            NetWinsSimTier ??= "Gold";
            PlacementsSimQuantity = placements.DefaultQuantity;
            NetWinsSimQuantity = netWins.DefaultQuantity;
        }
        finally
        {
            _loading = false;
        }

        Simulate();
    }

    /// <summary>Copies the editable fields back into the settings object.</summary>
    public void Apply()
    {
        Settings.Placements.Enabled = PlacementsEnabled;
        Settings.Placements.BasePrice = PlacementsBase;
        Settings.Placements.DefaultQuantity = Math.Max(1, PlacementsDefaultQuantity);
        Settings.Placements.MaxQuantity = Math.Max(0, PlacementsMaxQuantity);

        Settings.NetWins.Enabled = NetWinsEnabled;
        Settings.NetWins.BasePrice = NetWinsBase;
        Settings.NetWins.DefaultQuantity = Math.Max(1, NetWinsDefaultQuantity);
        Settings.NetWins.MaxQuantity = Math.Max(0, NetWinsMaxQuantity);
    }

    partial void OnPlacementsEnabledChanged(bool value) => ApplyAndSimulate();
    partial void OnPlacementsBaseChanged(decimal value) => ApplyAndSimulate();
    partial void OnPlacementsDefaultQuantityChanged(int value) => ApplyAndSimulate();
    partial void OnPlacementsMaxQuantityChanged(int value) => ApplyAndSimulate();
    partial void OnPlacementsSimTierChanged(string? value) => Simulate();
    partial void OnPlacementsSimQuantityChanged(int value) => Simulate();

    partial void OnNetWinsEnabledChanged(bool value) => ApplyAndSimulate();
    partial void OnNetWinsBaseChanged(decimal value) => ApplyAndSimulate();
    partial void OnNetWinsDefaultQuantityChanged(int value) => ApplyAndSimulate();
    partial void OnNetWinsMaxQuantityChanged(int value) => ApplyAndSimulate();
    partial void OnNetWinsSimTierChanged(string? value) => Simulate();
    partial void OnNetWinsSimQuantityChanged(int value) => Simulate();

    private void OnEdited()
    {
        if (!_loading)
        {
            Simulate();
            _host.Touch();
        }
    }

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

    public void Simulate()
    {
        (PlacementsSimTotal, PlacementsSimDetail) =
            Run(BoostingCategoryKind.Placements, PlacementsSimTier, PlacementsSimQuantity);

        (NetWinsSimTotal, NetWinsSimDetail) =
            Run(BoostingCategoryKind.NetWins, NetWinsSimTier, NetWinsSimQuantity);
    }

    private (string Total, string Detail) Run(BoostingCategoryKind kind, string? tier, int quantity)
    {
        if (tier is null)
        {
            return ("—", "Scegli un rank");
        }

        var quote = BoostingPriceCalculator.QuoteUnits(kind, tier, quantity, [], Settings);
        return quote.IsPriceable
            ? (quote.TotalText, string.Join("  ·  ", quote.Lines.Select(l => $"{l.Label} {l.Amount:N2}")))
            : ("—", quote.Problem ?? "non quotabile");
    }

    [RelayCommand]
    private void Save()
    {
        Apply();
        _host.Save();
    }

    [RelayCommand]
    private void ResetPlacements()
    {
        Settings.Placements = UnitPricing.CreatePlacementsDefault();
        _host.Save();
        Reload();
    }

    [RelayCommand]
    private void ResetNetWins()
    {
        Settings.NetWins = UnitPricing.CreateNetWinsDefault();
        _host.Save();
        Reload();
    }
}
