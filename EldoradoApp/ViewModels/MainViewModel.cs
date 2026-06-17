using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;

namespace EldoradoApp.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IEldoradoClient _client;

    public ObservableCollection<Order> Orders { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public MainViewModel(IEldoradoClient client)
    {
        _client = client;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Caricamento ordini...";
            var orders = await _client.GetOrdersAsync(cancellationToken);
            Orders.Clear();
            foreach (var order in orders)
            {
                Orders.Add(order);
            }
            StatusMessage = $"{orders.Count} ordini caricati alle {DateTimeOffset.Now:HH:mm:ss}";
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

    private bool CanRefresh() => !IsLoading;
}
