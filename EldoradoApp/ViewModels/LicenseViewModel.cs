using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;
using EldoradoApp.Services.Licensing;

namespace EldoradoApp.ViewModels;

/// <summary>
/// Drives both licence screens: the gate shown before the app opens, and the "Licenza"
/// page used later to renew or move the key. They ask the same questions, so they share
/// one view model rather than drifting into two.
/// </summary>
public sealed partial class LicenseViewModel : ObservableObject
{
    private readonly LicenseService _license;
    private readonly RemoteEntitlementService? _remote;
    private readonly Dispatcher? _dispatcher;

    /// <summary>Raised when a key has just been accepted — the gate window closes on this.</summary>
    public event Action? Activated;

    /// <summary>
    /// The activation gate constructs this before the shell exists, so the control plane
    /// is optional: without it the server card simply stays hidden.
    /// </summary>
    public LicenseViewModel(
        LicenseService license,
        RemoteEntitlementService? remote = null,
        Dispatcher? dispatcher = null)
    {
        _license = license;
        _remote = remote;
        _dispatcher = dispatcher;

        _license.Changed += Sync;

        if (_remote is not null)
        {
            _remote.Changed += OnRemoteChanged;
        }

        Sync();
        SyncRemote();
    }

    public LicenseService Service => _license;

    [ObservableProperty] private string _keyInput = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Short verdict, e.g. "Licenza attiva" — the headline of both screens.</summary>
    [ObservableProperty] private string _headline = "";

    /// <summary>The full explanation, including what to do about it.</summary>
    [ObservableProperty] private string _detail = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsLocked))] private bool _isValid;

    /// <summary>True when a valid licence is about to run out (drives the amber banner).</summary>
    [ObservableProperty] private bool _isExpiringSoon;

    /// <summary>Set after a failed paste, so the box can go red without touching the verdict.</summary>
    [ObservableProperty] private bool _hasError;

    [ObservableProperty] private string _keyId = "-";
    [ObservableProperty] private string _expiry = "-";
    [ObservableProperty] private string _daysLeftText = "-";
    [ObservableProperty] private string _deviceText = "-";

    /// <summary>Feedback for the copy buttons ("copiato ✓"), cleared on the next action.</summary>
    [ObservableProperty] private string _copyHint = "";

    public bool IsLocked => !IsValid;

    // ---- Optional server control plane ----

    /// <summary>False in the activation gate, which has no control plane to talk to.</summary>
    public bool HasRemote => _remote is not null;

    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private bool _requireServer;
    [ObservableProperty] private bool _syncConfiguration = true;
    [ObservableProperty] private string _serverStatus = "";
    [ObservableProperty] private bool _isRemoteBusy;

    /// <summary>True once a device token for the configured server is stored on this PC.</summary>
    [ObservableProperty] private bool _hasDeviceSession;

    /// <summary>What the buyer sends over so a key can be minted for this PC.</summary>
    public string MachineId => _license.MachineId;

    public string DiscordContact => LicenseOptions.DiscordContact;

    public bool HasDiscordInvite => LicenseOptions.DiscordInvite.Trim().Length > 0;

    /// <summary>Pulls every display property back from the service. Cheap; called on any change.</summary>
    private void Sync()
    {
        var info = _license.Info;

        IsValid = _license.IsValid;
        IsExpiringSoon = _license.IsExpiringSoon;

        Headline = _license.State switch
        {
            LicenseState.Valid when _license.IsExpiringSoon => "Licenza in scadenza",
            LicenseState.Valid => "Licenza attiva",
            LicenseState.Expired => "Licenza scaduta",
            LicenseState.WrongDevice => "Chiave di un altro PC",
            LicenseState.Revoked => "Chiave annullata",
            LicenseState.ClockTampered => "Data e ora non corrette",
            LicenseState.Malformed => "Chiave non valida",
            _ => "Attivazione richiesta"
        };

        // A failed paste has its own message; otherwise report the stored licence.
        Detail = _license.ActivationMessage.Length > 0 && !_license.IsValid
            ? _license.ActivationMessage
            : _license.Message;

        KeyId = info?.KeyId ?? "-";
        Expiry = info is null ? "-" : _license.ExpiryText;
        DaysLeftText = _license.IsValid ? $"{_license.DaysLeft}" : "0";
        DeviceText = info is null
            ? "-"
            : info.IsDeviceLocked ? "Bloccata su questo PC" : "Valida su qualsiasi PC";

        OnPropertyChanged(nameof(MachineId));
    }

    /// <summary>The control plane answers on a background thread; the bindings live on the UI one.</summary>
    private void OnRemoteChanged()
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            SyncRemote();
            return;
        }

        _dispatcher.BeginInvoke(SyncRemote);
    }

    private void SyncRemote()
    {
        if (_remote is null)
        {
            return;
        }

        ServerUrl = _remote.BaseUrl;
        RequireServer = _remote.IsRequired;
        SyncConfiguration = _remote.SyncsConfiguration;
        ServerStatus = _remote.StatusText;
        HasDeviceSession = _remote.HasDeviceToken;
    }

    /// <summary>Stores the endpoint. Changing it deliberately drops the old device token.</summary>
    [RelayCommand]
    private void SaveServer()
    {
        if (_remote is null)
        {
            return;
        }

        _remote.Configure(ServerUrl, RequireServer, SyncConfiguration);
        SyncRemote();
        CopyHint = "Impostazioni server salvate ✓";
    }

    /// <summary>
    /// Exchanges the licence already stored on this PC for a revocable device token. It
    /// deliberately re-uses the stored key rather than asking for it again: the key the
    /// server must see is the one this installation is actually running on.
    /// </summary>
    [RelayCommand]
    private async Task ActivateOnServerAsync()
    {
        if (_remote is null)
        {
            return;
        }

        if (_license.ActiveKeyForServerActivation is not { Length: > 0 } key)
        {
            ServerStatus = "Attiva prima una licenza su questo PC, poi collegala al server.";
            return;
        }

        _remote.Configure(ServerUrl, RequireServer, SyncConfiguration);

        IsRemoteBusy = true;

        try
        {
            var result = await _remote.ActivateAsync(key, _license.MachineId);
            ServerStatus = result.Message;
            HasDeviceSession = _remote.HasDeviceToken;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshServerAsync()
    {
        if (_remote is null)
        {
            return;
        }

        IsRemoteBusy = true;

        try
        {
            var result = await _remote.RefreshAsync();
            ServerStatus = result.Message;
            HasDeviceSession = _remote.HasDeviceToken;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    /// <summary>Drops the device token so this PC can be re-activated, or moved to another server.</summary>
    [RelayCommand]
    private void ForgetDeviceSession()
    {
        _remote?.ForgetDeviceSession();
        SyncRemote();
        CopyHint = "Sessione server rimossa da questo PC ✓";
    }

    [RelayCommand]
    private void Activate()
    {
        CopyHint = "";
        IsBusy = true;

        try
        {
            var state = _license.Activate(KeyInput);
            HasError = state != LicenseState.Valid;
            Sync();

            if (state != LicenseState.Valid)
            {
                return;
            }

            KeyInput = "";
            Activated?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Straight from the Discord message into the box, so nothing is lost in a manual copy.</summary>
    [RelayCommand]
    private void PasteKey()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                KeyInput = Clipboard.GetText().Trim();
                HasError = false;
                CopyHint = "";
            }
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Clipboard read failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CopyMachineId() => Copy(MachineId, "ID macchina copiato");

    [RelayCommand]
    private void CopyDiscord() => Copy(DiscordContact, "Contatto Discord copiato");

    [RelayCommand]
    private void OpenDiscord()
    {
        if (!HasDiscordInvite)
        {
            CopyDiscord();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = LicenseOptions.DiscordInvite, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CopyHint = $"Impossibile aprire Discord: {ex.Message}";
        }
    }

    /// <summary>Re-checks now: after a renewal, or once a wrong clock has been fixed.</summary>
    [RelayCommand]
    private async Task RecheckAsync()
    {
        IsBusy = true;

        try
        {
            await _license.RefreshRevocationAsync(force: true);
            _license.Refresh();
            Sync();
            CopyHint = "Controllo eseguito";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Unbinds this PC so the same key can be re-issued for another one.</summary>
    [RelayCommand]
    private void RemoveLicense()
    {
        if (MessageBox.Show(
                "Rimuovo la licenza da questo PC? Per rientrare servirà di nuovo la chiave.",
                "Eldorado Bot", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _license.Remove();
        KeyInput = "";
        HasError = false;
        Sync();
    }

    private void Copy(string text, string hint)
    {
        try
        {
            Clipboard.SetText(text);
            CopyHint = hint + " ✓";
        }
        catch (Exception ex)
        {
            CopyHint = $"Copia non riuscita: {ex.Message}";
        }
    }
}
