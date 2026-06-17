using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

public sealed partial class BoostingBotViewModel : ObservableObject
{
    private const int MaxLogItems = 300;

    private readonly EldoradoBackend _backend;
    private readonly AutoOfferEngine _engine;
    private readonly Dispatcher _dispatcher;
    private BoostingBotSettings _settings;
    private CancellationTokenSource? _loopCts;

    /// <summary>Per-tier price/hours/exclude rows (Iron … Radiant).</summary>
    public ObservableCollection<TierConfigRow> TierRows { get; } = [];
    public ObservableCollection<AutoOfferLogItem> Activity { get; } = [];

    // ---- Connection ----
    [ObservableProperty] private string _email = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotSignedIn))] private bool _isSignedIn;
    [ObservableProperty] private string _connectionStatus = "Non connesso";
    [ObservableProperty] private bool _isBusy;

    // ---- Bot config ----
    [ObservableProperty] private bool _botEnabled;
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private int _pollIntervalSeconds;
    [ObservableProperty] private string _currency = "USD";
    [ObservableProperty] private string _gameId = "";
    [ObservableProperty] private decimal _flatFee;
    [ObservableProperty] private string _acceptedRegionsText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotRunning))] private bool _isRunning;

    public bool NotSignedIn => !IsSignedIn;
    public bool NotRunning => !IsRunning;

    public BoostingBotViewModel(EldoradoBackend backend)
    {
        _backend = backend;
        _dispatcher = Application.Current.Dispatcher;
        _settings = BoostingBotSettingsStore.Load();
        _engine = backend.CreateAutoOfferEngine(() => _settings);
        _engine.Activity += OnEngineActivity;

        _email = CredentialStore.Load()?.Email ?? "";
        _isSignedIn = backend.IsSignedIn;
        _connectionStatus = _isSignedIn ? "Connesso" : "Non connesso";

        LoadSettingsIntoFields();
    }

    private void LoadSettingsIntoFields()
    {
        BotEnabled = _settings.Enabled;
        DryRun = _settings.DryRun;
        PollIntervalSeconds = _settings.PollIntervalSeconds;
        Currency = _settings.Currency;
        GameId = _settings.GameId ?? "";
        FlatFee = _settings.FlatFee;
        AcceptedRegionsText = string.Join(", ", _settings.AcceptedRegions);

        TierRows.Clear();
        foreach (var tier in ValorantRanks.Tiers)
        {
            TierRows.Add(new TierConfigRow(
                tier,
                _settings.PricePerDivision(tier),
                _settings.HoursPerDivision(tier),
                _settings.IsRankExcluded(tier)));
        }
    }

    private void ApplyFieldsToSettings()
    {
        _settings.Enabled = BotEnabled;
        _settings.DryRun = DryRun;
        _settings.PollIntervalSeconds = PollIntervalSeconds <= 0 ? 15 : PollIntervalSeconds;
        _settings.Currency = string.IsNullOrWhiteSpace(Currency) ? "USD" : Currency.Trim();
        _settings.GameId = string.IsNullOrWhiteSpace(GameId) ? null : GameId.Trim();
        _settings.FlatFee = FlatFee;

        _settings.AcceptedRegions = AcceptedRegionsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        _settings.ExcludedRanks = TierRows.Where(r => r.Excluded).Select(r => r.Tier).ToList();

        foreach (var row in TierRows)
        {
            _settings.PricePerDivisionByTier[row.Tier] = row.PricePerDivision;
            _settings.HoursPerDivisionByTier[row.Tier] = row.HoursPerDivision;
        }
    }

    [RelayCommand]
    private async Task SignInAsync(string? password)
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(password))
        {
            ConnectionStatus = "Inserisci email e password";
            return;
        }

        try
        {
            IsBusy = true;
            ConnectionStatus = "Accesso in corso…";
            await _backend.SignInAndRememberAsync(Email.Trim(), password);
            IsSignedIn = true;
            ConnectionStatus = "Connesso ✓";
        }
        catch (Exception ex)
        {
            IsSignedIn = false;
            ConnectionStatus = $"Accesso fallito: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        if (IsRunning)
        {
            StopBot();
        }

        _backend.SignOut();
        IsSignedIn = false;
        ConnectionStatus = "Disconnesso";
    }

    [RelayCommand]
    private void Save()
    {
        ApplyFieldsToSettings();
        BoostingBotSettingsStore.Save(_settings);
        ConnectionStatus = "Impostazioni salvate";
    }

    [RelayCommand]
    private void StartBot()
    {
        if (!IsSignedIn || IsRunning)
        {
            return;
        }

        BotEnabled = true;
        ApplyFieldsToSettings();
        BoostingBotSettingsStore.Save(_settings);

        _loopCts = new CancellationTokenSource();
        _ = _engine.RunLoopAsync(_loopCts.Token);
        IsRunning = true;
        ConnectionStatus = DryRun ? "Bot avviato (DRY-RUN)" : "Bot avviato · LIVE";
    }

    [RelayCommand]
    private void StopBot()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        IsRunning = false;
        BotEnabled = false;
        ConnectionStatus = "Bot fermato";
    }

    private void OnEngineActivity(AutoOfferEvent e)
    {
        _dispatcher.Invoke(() =>
        {
            Activity.Insert(0, new AutoOfferLogItem(e));
            while (Activity.Count > MaxLogItems)
            {
                Activity.RemoveAt(Activity.Count - 1);
            }
        });
    }
}
