using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

/// <summary>
/// Main-screen live feed of incoming boosting requests routed to the seller, with
/// each request's rank range/region parsed and the offer (price + time) computed.
/// Requires sign-in (done from the bot window); the backend is shared via the App.
/// </summary>
public sealed partial class RequestsFeedViewModel : ObservableObject
{
    private readonly EldoradoBackend _backend;
    private readonly DispatcherTimer _timer;
    private BoostingBotSettings _settings;

    public ObservableCollection<RequestRow> Requests { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotSignedIn))] private bool _isSignedIn;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Pronto";
    [ObservableProperty] private int _count;

    public bool NotSignedIn => !IsSignedIn;

    public RequestsFeedViewModel(EldoradoBackend backend)
    {
        _backend = backend;
        _settings = BoostingBotSettingsStore.Load();
        _isSignedIn = backend.IsSignedIn;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public async Task InitializeAsync() => await RefreshAsync();

    partial void OnIsLiveChanged(bool value)
    {
        if (value)
        {
            _timer.Start();
            StatusMessage = "Live · aggiornamento richieste…";
        }
        else
        {
            _timer.Stop();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Re-read settings so price/filters reflect the latest bot config.
        _settings = BoostingBotSettingsStore.Load();
        IsSignedIn = _backend.IsSignedIn;

        if (!IsSignedIn)
        {
            Requests.Clear();
            Count = 0;
            StatusMessage = "Accedi dal Bot per vedere le richieste.";
            return;
        }

        try
        {
            IsLoading = true;
            var gameId = string.IsNullOrWhiteSpace(_settings.GameId) ? null : _settings.GameId;
            var items = await _backend.BoostingRequests
                .GetReceivedRequestsAsync(BoostingRequestFilter.ActiveRequests, gameId, 50);

            Requests.Clear();
            foreach (var r in items)
            {
                Requests.Add(new RequestRow(r, _settings));
            }

            Count = Requests.Count;
            StatusMessage = $"{Count} richieste · aggiornato {DateTimeOffset.Now:HH:mm:ss}";
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
}
