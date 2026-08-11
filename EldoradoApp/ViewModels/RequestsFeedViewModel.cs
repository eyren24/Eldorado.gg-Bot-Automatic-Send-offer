using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>
/// The dashboard's live feed of incoming boosting requests, each priced with the very
/// same calculator the bot uses — so what you see is what would be offered.
/// </summary>
public sealed partial class RequestsFeedViewModel : ObservableObject
{
    private readonly EldoradoBackend _backend;
    private readonly SettingsHost _host;
    private readonly DispatcherTimer _timer;

    private BoostingBotSettings Settings => _host.Settings;

    public ObservableCollection<RequestRow> Requests { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotSignedIn))] private bool _isSignedIn;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Pronto";
    [ObservableProperty] private int _count;
    [ObservableProperty] private int _priceableCount;
    [ObservableProperty] private string _lastUpdate = "—";
    [ObservableProperty] private RequestRow? _selected;

    public bool NotSignedIn => !IsSignedIn;

    public RequestsFeedViewModel(EldoradoBackend backend, SettingsHost host)
    {
        _backend = backend;
        _host = host;
        _isSignedIn = backend.IsSignedIn;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) => await RefreshAsync();

        // Re-price the visible cards whenever the price list changes.
        _host.Changed += RepriceInPlace;
    }

    public async Task InitializeAsync() => await RefreshAsync();

    partial void OnIsLiveChanged(bool value)
    {
        if (value)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(5, Settings.PollIntervalSeconds));
            _timer.Start();
            StatusMessage = "Live · aggiornamento richieste…";
        }
        else
        {
            _timer.Stop();
            StatusMessage = "Aggiornamento automatico fermo";
        }
    }

    /// <summary>Cheap refresh: recompute prices for the cards already on screen.</summary>
    private void RepriceInPlace()
    {
        if (_rawRequests.Count == 0)
        {
            return;
        }

        Rebuild(_rawRequests);
    }

    private List<BoostingRequest> _rawRequests = [];

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsSignedIn = _backend.IsSignedIn;

        if (!IsSignedIn)
        {
            Requests.Clear();
            _rawRequests = [];
            Count = 0;
            PriceableCount = 0;
            StatusMessage = "Accedi dalla scheda Account per vedere le richieste.";
            return;
        }

        try
        {
            IsLoading = true;
            var gameId = string.IsNullOrWhiteSpace(Settings.GameId) ? null : Settings.GameId;
            var items = await _backend.BoostingRequests
                .GetReceivedRequestsAsync(BoostingRequestFilter.ActiveRequests, gameId, 50);

            _rawRequests = items.ToList();
            Rebuild(_rawRequests);

            LastUpdate = DateTimeOffset.Now.ToString("HH:mm:ss");
            StatusMessage = $"{Count} richieste · {PriceableCount} quotabili · aggiornato {LastUpdate}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Rebuild(IReadOnlyList<BoostingRequest> items)
    {
        var selectedTitle = Selected?.CategoryTitle;

        Requests.Clear();
        foreach (var request in items)
        {
            Requests.Add(new RequestRow(request, Settings));
        }

        Count = Requests.Count;
        PriceableCount = Requests.Count(r => r.PriceText != "—");
        Selected = Requests.FirstOrDefault(r => r.CategoryTitle == selectedTitle);
    }
}
