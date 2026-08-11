using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>
/// The surcharge list for buyer-requested options. Each row carries the words that
/// trigger it, so "DuoQ" or "with stream" anywhere in a request is priced automatically.
/// The tester at the bottom shows, for a pasted request title, which extras fire and
/// what the buyer would actually be quoted.
/// </summary>
public sealed partial class ExtrasViewModel : ObservableObject
{
    private readonly SettingsHost _host;

    private BoostingBotSettings Settings => _host.Settings;

    public ObservableCollection<ExtraOptionRow> Extras { get; } = [];

    public Array Kinds { get; } = Enum.GetValues(typeof(ExtraKind));

    [ObservableProperty] private ExtraOptionRow? _selected;

    // ---- Tester ----
    [ObservableProperty] private string _testText = "Valorant Rank Boost EU Gold 1 to Platinum 2 duoq with stream";
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private string _testTotal = "—";

    public ExtrasViewModel(SettingsHost host)
    {
        _host = host;
        Reload();
    }

    public void Reload()
    {
        Extras.Clear();
        foreach (var extra in Settings.Extras)
        {
            Extras.Add(new ExtraOptionRow(extra, OnRowEdited));
        }

        RunTest();
    }

    private void OnRowEdited()
    {
        RunTest();
        _host.Touch();
    }

    [RelayCommand]
    private void Add()
    {
        var model = new ExtraOption { Label = "Nuovo extra", Keywords = "", Amount = 5m };
        Settings.Extras.Add(model);

        var row = new ExtraOptionRow(model, OnRowEdited);
        Extras.Add(row);
        Selected = row;
        _host.Save();
    }

    [RelayCommand]
    private void Remove(ExtraOptionRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        Settings.Extras.Remove(row.Model);
        Extras.Remove(row);
        _host.Save();
        RunTest();
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        Settings.Extras = ExtraOption.CreateDefaults();
        _host.Save();
        Reload();
    }

    [RelayCommand]
    private void Save()
    {
        _host.Save();
        RunTest();
    }

    /// <summary>Runs the parser + calculator over <see cref="TestText"/> exactly as the bot would.</summary>
    [RelayCommand]
    private void RunTest()
    {
        var probe = new BoostingRequest(
            Id: "test", GameId: null, BoostingCategoryId: null,
            BoostingCategoryTitle: TestText, BuyerId: null, BuyerUsername: "tester",
            IsBuyerMuted: false, CreatedDate: DateTimeOffset.Now);

        var parsed = BoostingCategoryParser.Parse(probe, Settings);
        var matched = Settings.Extras
            .Where(e => parsed.MatchedExtraIds.Contains(e.Id))
            .Select(e => e.Label)
            .ToList();

        var quote = BoostingPriceCalculator.Quote(
            parsed.CurrentRank, parsed.DesiredRank, parsed.MatchedExtraIds, Settings);

        var rank = parsed.HasRange ? $"{parsed.CurrentRank} → {parsed.DesiredRank}" : "range non riconosciuto";
        var region = parsed.Region is null ? "" : $" · regione {parsed.Region}";
        var extras = matched.Count == 0 ? "nessun extra rilevato" : $"extra: {string.Join(", ", matched)}";

        TestResult = $"{rank}{region} · {extras}";
        TestTotal = quote.IsPriceable ? quote.TotalText : "—";
    }

    partial void OnTestTextChanged(string value) => RunTest();
}
