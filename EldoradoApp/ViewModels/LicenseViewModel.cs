using System.Diagnostics;
using System.Windows;
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

    /// <summary>Raised when a key has just been accepted — the gate window closes on this.</summary>
    public event Action? Activated;

    public LicenseViewModel(LicenseService license)
    {
        _license = license;
        _license.Changed += Sync;
        Sync();
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
