using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>
/// Seller account + polling configuration: the three ways to sign in (email/password,
/// Google, hand-pasted token), which game the bot watches, and the per-category
/// switches that decide which requests it answers.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly EldoradoBackend _backend;
    private readonly SettingsHost _host;

    private IReadOnlyList<BoostingSubscription> _subscriptions = [];
    private string? _rowsGameId;
    private bool _loadingGames;

    private BoostingBotSettings Settings => _host.Settings;

    /// <summary>Raised when sign-in state changes, so the shell can refresh the feed.</summary>
    public event Action? SignedInChanged;

    public ObservableCollection<GameOption> Games { get; } = [];
    public ObservableCollection<CategoryPricingRow> Categories { get; } = [];
    public Array DeliveryTimeOptions { get; } = Enum.GetValues(typeof(BoostingDeliveryTime));
    public Array CategoryKinds { get; } = Enum.GetValues(typeof(BoostingCategoryKind));

    // ---- Connection ----
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _manualTokenInput = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotSignedIn))] private bool _isSignedIn;
    [ObservableProperty] private string _connectionStatus = "Non connesso";
    [ObservableProperty] private bool _isBusy;

    // ---- Polling ----
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private int _pollIntervalSeconds;
    [ObservableProperty] private GameOption? _selectedGame;
    [ObservableProperty] private string _gameStatus = "Gioco non ancora rilevato";
    [ObservableProperty] private bool _answerUnknownCategories;

    public bool NotSignedIn => !IsSignedIn;

    public AccountViewModel(EldoradoBackend backend, SettingsHost host)
    {
        _backend = backend;
        _host = host;

        _email = CredentialStore.Load()?.Email ?? "";
        _isSignedIn = backend.IsSignedIn;
        _connectionStatus = DescribeConnection();

        Reload();
    }

    public void Reload()
    {
        DryRun = Settings.DryRun;
        PollIntervalSeconds = Settings.PollIntervalSeconds;
        AnswerUnknownCategories = Settings.AnswerUnknownCategories;
    }

    private string DescribeConnection() => _backend.IsSignedIn
        ? _backend.AuthMethod switch
        {
            SellerAuthMethod.Google => "Connesso ✓ (Google)",
            SellerAuthMethod.ManualToken => "Connesso ✓ (token)",
            SellerAuthMethod.EmailPassword => "Connesso ✓ (email/password)",
            _ => "Connesso ✓"
        }
        : "Non connesso";

    /// <summary>Refreshes the connection state from the backend (after a restore at startup).</summary>
    public void SyncConnection()
    {
        IsSignedIn = _backend.IsSignedIn;
        ConnectionStatus = DescribeConnection();
    }

    public void Apply()
    {
        Settings.DryRun = DryRun;
        Settings.PollIntervalSeconds = PollIntervalSeconds <= 0 ? 15 : PollIntervalSeconds;
        Settings.GameId = SelectedGame?.Id;
        Settings.AnswerUnknownCategories = AnswerUnknownCategories;

        PersistCurrentRows();
    }

    partial void OnDryRunChanged(bool value) => ApplyAndSave();
    partial void OnPollIntervalSecondsChanged(int value) => ApplyAndSave();
    partial void OnAnswerUnknownCategoriesChanged(bool value) => ApplyAndSave();

    private void ApplyAndSave()
    {
        if (_loadingGames)
        {
            return;
        }

        Apply();
        _host.Save();
    }

    /// <summary>
    /// Persists a category edit immediately. The rows already wrote the value into the
    /// settings object, so this only has to put it on disk — but it has to, because the
    /// seller has no way of knowing that "Salva listino" on the Prezzi page doesn't cover
    /// this grid.
    /// </summary>
    private void OnCategoryRowEdited()
    {
        if (_loadingGames)
        {
            return;
        }

        _host.Save();
    }

    /// <summary>
    /// Installs the shown category rows into settings, preserving the other games.
    /// </summary>
    /// <remarks>
    /// The rows write straight through to these <see cref="CategoryPricing"/> instances, so
    /// the ones the settings hold have to be the very same objects — otherwise an edit lands
    /// on an object nothing ever serialises, which is exactly the bug this replaced.
    /// </remarks>
    private void PersistCurrentRows()
    {
        if (_rowsGameId is null)
        {
            return;
        }

        var others = Settings.CategoryPrices
            .Where(c => !string.Equals(c.GameId, _rowsGameId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        others.AddRange(Categories.Select(r => r.Model));
        Settings.CategoryPrices = others;
    }

    /// <summary>Rebuilds the category rows for the selected game, merging saved values.</summary>
    private void BuildCategoryRows()
    {
        Categories.Clear();
        _rowsGameId = SelectedGame?.Id;
        if (_rowsGameId is null)
        {
            return; // "all games" — no single category list to configure
        }

        foreach (var category in _subscriptions
                     .Where(s => string.Equals(s.GameId, _rowsGameId, StringComparison.OrdinalIgnoreCase) && s.IsSubscribed)
                     .GroupBy(s => s.BoostingCategoryId)
                     .Select(g => g.First()))
        {
            var saved = Settings.ForCategory(_rowsGameId, category.BoostingCategoryId);
            var name = category.BoostingCategoryName ?? category.BoostingCategoryId ?? "";

            Categories.Add(new CategoryPricingRow(new CategoryPricing
            {
                GameId = _rowsGameId,
                CategoryId = category.BoostingCategoryId ?? "",
                CategoryName = name,
                Enabled = saved?.Enabled ?? true,
                Kind = saved?.Kind ?? Settings.KindFor(_rowsGameId, category.BoostingCategoryId, name),
                FlatPrice = saved?.FlatPrice ?? 0m,
                Quantity = saved?.Quantity ?? 1,
                MinQuantity = saved?.MinQuantity ?? 1,
                DeliveryTime = saved?.DeliveryTime ?? Settings.DefaultDeliveryTime,
            }, OnCategoryRowEdited));
        }

        // Adopt the freshly built rows right away: from here on the grid edits the objects
        // the settings actually hold.
        PersistCurrentRows();
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
            SignedInChanged?.Invoke();
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

            var expiry = _backend.ManualToken.ExpiresAt?.ToLocalTime();
            ConnectionStatus = _backend.ManualToken.IsExpired
                ? "Token impostato ma GIÀ SCADUTO — incollane uno fresco"
                : expiry is null
                    ? "Connesso ✓ (token)"
                    : $"Connesso ✓ (token, scade alle {expiry:HH:mm})";
            SignedInChanged?.Invoke();
        }
        catch (Exception ex)
        {
            IsSignedIn = false;
            ConnectionStatus = $"Token non valido: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads the seller's boosting subscriptions and locks the bot onto Valorant — the
    /// only game it is meant to work on. The dropdown lists other games only if Valorant
    /// isn't among the subscriptions, so the problem is visible instead of silent.
    /// </summary>
    public async Task LoadGamesAsync()
    {
        _loadingGames = true;
        try
        {
            Games.Clear();

            try
            {
                _subscriptions = await _backend.BoostingOffers.GetSubscriptionsAsync();
            }
            catch (Exception ex)
            {
                ApiLog.Write($"LoadGames failed: {ex.Message}");
                _subscriptions = [];
            }

            var all = _subscriptions
                .Where(s => !string.IsNullOrWhiteSpace(s.GameId))
                .GroupBy(s => s.GameId)
                .Select(g => new GameOption(g.Key, g.First().GameName ?? g.Key!))
                .OrderBy(g => g.Name)
                .ToList();

            var valorant = all.FirstOrDefault(g =>
                g.Name.Contains("valorant", StringComparison.OrdinalIgnoreCase));

            if (valorant is not null)
            {
                Games.Add(valorant);
                SelectedGame = valorant;
                Settings.GameId = valorant.Id;
                GameStatus = $"Bloccato su {valorant.Name} (id {valorant.Id}).";
            }
            else
            {
                foreach (var game in all)
                {
                    Games.Add(game);
                }

                SelectedGame = Games.FirstOrDefault(g => g.Id == Settings.GameId) ?? Games.FirstOrDefault();
                Settings.GameId = SelectedGame?.Id;
                GameStatus = _subscriptions.Count == 0
                    ? "Nessuna iscrizione trovata: accedi e ricarica."
                    : "Valorant non è tra le tue iscrizioni boosting — iscriviti dal sito Eldorado, poi ricarica.";
            }

            BuildCategoryRows();
            _host.Save();
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

        // Keep edits for the previous game, switch the list, and persist the choice so
        // the feed (which re-reads settings on every refresh) follows along.
        PersistCurrentRows();
        Settings.GameId = value?.Id;
        BuildCategoryRows();
        _host.Save();
    }

    [RelayCommand]
    private void SignOut()
    {
        _backend.SignOut();
        IsSignedIn = false;
        ConnectionStatus = "Disconnesso";
        Games.Clear();
        Categories.Clear();
        SignedInChanged?.Invoke();
    }

    [RelayCommand]
    private void Save()
    {
        Apply();
        _host.Save();
        ConnectionStatus = "Impostazioni salvate ✓";
    }

    [RelayCommand]
    private async Task ReloadCategoriesAsync() => await LoadGamesAsync();
}
