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

    /// <summary>Per-category price rows for the selected game (from boosting subscriptions).</summary>
    public ObservableCollection<CategoryPricingRow> CategoryRows { get; } = [];
    public ObservableCollection<AutoOfferLogItem> Activity { get; } = [];

    /// <summary>Delivery-time choices for the per-category dropdown.</summary>
    public Array DeliveryTimeOptions { get; } = Enum.GetValues(typeof(BoostingDeliveryTime));

    private IReadOnlyList<BoostingSubscription> _subscriptions = [];
    private string? _rowsGameId;

    // ---- Connection ----
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _manualTokenInput = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotSignedIn))] private bool _isSignedIn;
    [ObservableProperty] private string _connectionStatus = "Non connesso";
    [ObservableProperty] private bool _isBusy;

    // ---- Bot config ----
    [ObservableProperty] private bool _botEnabled;
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private int _pollIntervalSeconds;
    [ObservableProperty] private string _currency = "USD";
    [ObservableProperty] private GameOption? _selectedGame;
    [ObservableProperty] private string _offerMessage = "";

    /// <summary>Games for the dropdown (from the seller's boosting subscriptions) + "all games".</summary>
    public ObservableCollection<GameOption> Games { get; } = [];

    private bool _loadingGames;
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
        _connectionStatus = _isSignedIn
            ? backend.AuthMethod switch
            {
                SellerAuthMethod.Google => "Connesso (Google)",
                SellerAuthMethod.ManualToken => "Connesso (token)",
                _ => "Connesso",
            }
            : "Non connesso";

        LoadSettingsIntoFields();
        _ = LoadGamesAsync();
    }

    private void LoadSettingsIntoFields()
    {
        BotEnabled = _settings.Enabled;
        DryRun = _settings.DryRun;
        PollIntervalSeconds = _settings.PollIntervalSeconds;
        Currency = _settings.Currency;
        OfferMessage = _settings.OfferMessage;
        AcceptedRegionsText = string.Join(", ", _settings.AcceptedRegions);
    }

    private void ApplyFieldsToSettings()
    {
        _settings.Enabled = BotEnabled;
        _settings.DryRun = DryRun;
        _settings.PollIntervalSeconds = PollIntervalSeconds <= 0 ? 15 : PollIntervalSeconds;
        _settings.Currency = string.IsNullOrWhiteSpace(Currency) ? "USD" : Currency.Trim();
        _settings.GameId = SelectedGame?.Id;
        _settings.OfferMessage = OfferMessage ?? "";

        _settings.AcceptedRegions = AcceptedRegionsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        PersistCurrentRows();
    }

    /// <summary>Writes the currently shown category rows back into settings (preserving other games).</summary>
    private void PersistCurrentRows()
    {
        var others = _settings.CategoryPrices
            .Where(c => !string.Equals(c.GameId, _rowsGameId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        others.AddRange(CategoryRows.Select(r => r.ToModel()));
        _settings.CategoryPrices = others;
    }

    /// <summary>Rebuilds the category rows for the selected game, merging saved prices.</summary>
    private void BuildCategoryRows()
    {
        CategoryRows.Clear();
        _rowsGameId = SelectedGame?.Id;
        if (_rowsGameId is null)
        {
            return; // "all games" — no single category list to configure
        }

        foreach (var cat in _subscriptions
                     .Where(s => string.Equals(s.GameId, _rowsGameId, StringComparison.OrdinalIgnoreCase) && s.IsSubscribed)
                     .GroupBy(s => s.BoostingCategoryId)
                     .Select(g => g.First()))
        {
            var saved = _settings.ForCategory(_rowsGameId, cat.BoostingCategoryId);
            var pricing = new CategoryPricing
            {
                GameId = _rowsGameId,
                CategoryId = cat.BoostingCategoryId ?? "",
                CategoryName = cat.BoostingCategoryName ?? cat.BoostingCategoryId ?? "",
                Enabled = saved?.Enabled ?? true,
                PricePerUnit = saved?.PricePerUnit ?? 0m,
                Quantity = saved?.Quantity ?? 1,
                MinQuantity = saved?.MinQuantity ?? 1,
                DeliveryTime = saved?.DeliveryTime ?? BoostingDeliveryTime.Day1,
            };
            CategoryRows.Add(new CategoryPricingRow(pricing));
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
            ConnectionStatus = "Connesso ✓ (email/password)";
            await LoadGamesAsync();
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

    /// <summary>
    /// Signs in with Google. <paramref name="showLogin"/> is supplied by the view: it opens
    /// the WebView2 Hosted-UI window for the given authorize URL and returns the captured code.
    /// Not a <see cref="RelayCommand"/> because it needs the view to host the browser window.
    /// </summary>
    public async Task SignInWithGoogleAsync(Func<string, (string? code, string? error)> showLogin)
    {
        try
        {
            IsBusy = true;
            ConnectionStatus = "Accesso Google…";
            await _backend.SignInWithGoogleAsync(showLogin);
            IsSignedIn = true;
            ConnectionStatus = "Connesso ✓ (Google)";
            await LoadGamesAsync();
        }
        catch (OperationCanceledException ex)
        {
            ConnectionStatus = $"Accesso Google annullato: {ex.Message}";
        }
        catch (Exception ex)
        {
            IsSignedIn = false;
            ConnectionStatus = $"Accesso Google fallito: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void UseManualToken()
    {
        if (string.IsNullOrWhiteSpace(ManualTokenInput))
        {
            ConnectionStatus = "Incolla un token (JWT) prima";
            return;
        }

        try
        {
            _backend.UseManualToken(ManualTokenInput);
            IsSignedIn = true;
            ManualTokenInput = "";
            _ = LoadGamesAsync();
            var exp = _backend.ManualToken.ExpiresAt?.ToLocalTime();
            ConnectionStatus = _backend.ManualToken.IsExpired
                ? "Token impostato ma GIÀ SCADUTO — incollane uno fresco"
                : exp is null
                    ? "Connesso ✓ (token)"
                    : $"Connesso ✓ (token, scade alle {exp:HH:mm})";
        }
        catch (Exception ex)
        {
            IsSignedIn = false;
            ConnectionStatus = $"Token non valido: {ex.Message}";
        }
    }

    /// <summary>Populates the game dropdown from the seller's boosting subscriptions (+ "all games").</summary>
    public async Task LoadGamesAsync()
    {
        _loadingGames = true;
        try
        {
            var current = _settings.GameId;
            Games.Clear();
            Games.Add(GameOption.All);

            try
            {
                _subscriptions = await _backend.BoostingOffers.GetSubscriptionsAsync();
                foreach (var game in _subscriptions
                             .Where(s => !string.IsNullOrWhiteSpace(s.GameId))
                             .GroupBy(s => s.GameId)
                             .Select(g => new GameOption(g.Key, g.First().GameName ?? g.Key!))
                             .OrderBy(g => g.Name))
                {
                    Games.Add(game);
                }
            }
            catch (Exception ex)
            {
                ApiLog.Write($"LoadGames failed: {ex.Message}");
            }

            SelectedGame = Games.FirstOrDefault(g => g.Id == current) ?? Games[0];
            BuildCategoryRows();
        }
        finally
        {
            _loadingGames = false;
        }
    }

    partial void OnSelectedGameChanged(GameOption? value)
    {
        if (_loadingGames)
        {
            return;
        }

        // Keep edits for the previous game, switch the list, and persist the chosen game
        // so the main feed (which reloads settings each refresh) picks it up.
        PersistCurrentRows();
        _settings.GameId = value?.Id;
        BuildCategoryRows();
        BoostingBotSettingsStore.Save(_settings);
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
