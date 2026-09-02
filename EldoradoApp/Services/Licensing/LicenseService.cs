using EldoradoApp.Models;

namespace EldoradoApp.Services.Licensing;

/// <summary>
/// The gate the whole app sits behind. It answers one question - is this copy licensed
/// right now? - and it answers it entirely offline, from the signature inside the key.
/// </summary>
/// <remarks>
/// Keys are minted outside the app by <c>tools/EldoradoKeygen</c>, which holds the only
/// private key. Nothing here can create or extend a licence, so there is no local edit,
/// no file swap and no registry trick that produces a working key: forging one means
/// forging an ECDSA P-256 signature.
/// </remarks>
public sealed class LicenseService
{
    private StoredLicense? _stored;

    /// <summary>Raised whenever the verdict changes, so the shell can lock the bot down.</summary>
    public event Action? Changed;

    public LicenseState State { get; private set; } = LicenseState.None;

    /// <summary>The decoded key, present as soon as the signature checks out - even if expired.</summary>
    public LicenseInfo? Info { get; private set; }

    /// <summary>Customer-facing Italian explanation of <see cref="State"/>.</summary>
    public string Message { get; private set; } = "Nessuna licenza attiva su questo PC.";

    /// <summary>
    /// What the last <see cref="Activate"/> attempt had to say. Kept apart from
    /// <see cref="Message"/> so a mistyped renewal reports its own problem instead of being
    /// papered over by the still-valid licence underneath it.
    /// </summary>
    public string ActivationMessage { get; private set; } = "";

    public bool IsValid => State == LicenseState.Valid;

    /// <summary>
    /// The already DPAPI-protected key is needed only to exchange a newly configured
    /// server session for a revocable device token. It is internal so UI code cannot
    /// accidentally display or log it.
    /// </summary>
    internal string? ActiveKeyForServerActivation => _stored?.Key;

    /// <summary>What the buyer sends over Discord to get a key minted for this PC.</summary>
    public string MachineId => HardwareId.Display;

    /// <summary>Whole days left on a valid licence; 0 otherwise.</summary>
    public int DaysLeft => State == LicenseState.Valid && Info is not null
        ? Info.DaysLeft(DateTimeOffset.UtcNow)
        : 0;

    /// <summary>True while a valid licence is close enough to its end to be worth nagging about.</summary>
    public bool IsExpiringSoon => State == LicenseState.Valid && DaysLeft <= LicenseOptions.RenewalWarningDays;

    /// <summary>Expiry in the customer's own time zone, ready to print.</summary>
    public string ExpiryText => Info is null
        ? "-"
        : Info.ExpiresAtUtc.ToLocalTime().AddSeconds(-1).ToString("dd/MM/yyyy");

    /// <summary>Reads the stored key at startup and decides where the app goes next.</summary>
    public LicenseState Initialize()
    {
        RevocationList.LoadCache();
        _stored = LicenseStore.Load();
        Evaluate();

        // Off the critical path on purpose: a slow gist must never delay the window.
        _ = RefreshRevocationAsync();

        return State;
    }

    /// <summary>Re-runs the verdict against the current clock and the current revocation list.</summary>
    public LicenseState Refresh()
    {
        Evaluate();
        return State;
    }

    /// <summary>
    /// Validates a pasted key and, if it holds up, stores it as this PC's licence.
    /// Returns the resulting state; <see cref="Message"/> explains a refusal.
    /// </summary>
    public LicenseState Activate(string? pastedKey)
    {
        var candidate = new StoredLicense
        {
            Key = (pastedKey ?? "").Trim(),
            ActivatedUtc = DateTimeOffset.UtcNow
        };

        var state = Judge(candidate, out var info, out var message);
        ActivationMessage = message;

        // A bad paste must not wipe a licence that is still working: on anything but a
        // clean pass the stored key is left exactly as it was.
        if (state != LicenseState.Valid)
        {
            Changed?.Invoke();
            return state;
        }

        _stored = candidate;
        LicenseStore.Save(candidate);

        // A freshly issued key that outlives everything the guard has seen is the one thing
        // allowed to relax it - it can only have come from the seller.
        if (info is not null)
        {
            TamperGuard.AcceptRenewal(info, DateTimeOffset.UtcNow);
        }

        Evaluate();

        ApiLog.Write($"License activated: {info?.KeyId} until {info?.Expires:yyyy-MM-dd}");
        return State;
    }

    /// <summary>Forgets the key on this PC (used when moving to another machine).</summary>
    public void Remove()
    {
        LicenseStore.Clear();
        _stored = null;
        ActivationMessage = "";
        Evaluate();
    }

    /// <summary>Pulls the revocation list, then re-checks. No-op when no URL is configured.</summary>
    public async Task RefreshRevocationAsync(bool force = false)
    {
        await RevocationList.RefreshAsync(force);

        if (RevocationList.IsLoaded)
        {
            Evaluate();
        }
    }

    private void Evaluate()
    {
        if (_stored is null)
        {
            State = LicenseState.None;
            Info = null;
            Message = LicenseOptions.IsConfigured
                ? "Nessuna licenza su questo PC: incolla la chiave che hai acquistato."
                : BuildNotConfiguredMessage();
        }
        else
        {
            State = Judge(_stored, out var info, out var message);
            Info = info;
            Message = message;

            // Only a genuine, device-matching key moves the guard: a key meant for another
            // PC says nothing about how much time has passed on this one.
            if (info is not null && State is LicenseState.Valid or LicenseState.Expired)
            {
                TamperGuard.Observe(info, DateTimeOffset.UtcNow);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The verdict itself, with no side effects, so <see cref="Activate"/> can trial-run a key.</summary>
    private static LicenseState Judge(StoredLicense stored, out LicenseInfo? info, out string message)
    {
        info = null;

        if (!LicenseOptions.IsConfigured)
        {
            message = BuildNotConfiguredMessage();
            return LicenseState.Malformed;
        }

        if (!LicenseCodec.TryDecode(stored.Key, out var payload, out var signature, out var decodeError))
        {
            message = decodeError;
            return LicenseState.Malformed;
        }

        if (!LicenseCodec.Verify(payload, signature, LicenseOptions.PublicKey))
        {
            message = "Chiave non riconosciuta: la firma non corrisponde. " +
                      "Ricopiala per intero, oppure richiedila su Discord.";
            return LicenseState.Malformed;
        }

        info = LicenseCodec.Read(payload);

        if (RevocationList.IsRevoked(info.KeyId))
        {
            message = $"Chiave {info.KeyId} annullata. Contattami su Discord ({LicenseOptions.DiscordContact}) " +
                      "se pensi sia un errore.";
            return LicenseState.Revoked;
        }

        if (!HardwareId.Matches(info.DeviceTag))
        {
            message = "Questa chiave e' stata emessa per un altro PC. " +
                      $"Scrivimi su Discord ({LicenseOptions.DiscordContact}) con l'ID macchina qui sotto " +
                      "e te la sposto.";
            return LicenseState.WrongDevice;
        }

        var now = DateTimeOffset.UtcNow;

        // The clock cannot legitimately read earlier than the day the key was minted.
        if (TamperGuard.PredatesIssue(info, now))
        {
            message = "Data e ora di Windows sono precedenti all'emissione della chiave. " +
                      "Rimetti l'orologio giusto (Impostazioni > Data e ora > Imposta ora automaticamente) " +
                      "e riapri l'app.";
            return LicenseState.ClockTampered;
        }

        // Already watched run out on this PC: no clock setting brings it back.
        if (TamperGuard.IsSpent(info) && now < info.ExpiresAtUtc)
        {
            message = "Questa chiave risulta gia' consumata su questo PC, ma l'orologio dice il contrario. " +
                      "Rimetti data e ora corrette; se sono giuste, la chiave e' finita e va rinnovata " +
                      $"su Discord ({LicenseOptions.DiscordContact}).";
            return LicenseState.ClockTampered;
        }

        if (now >= info.ExpiresAtUtc || TamperGuard.IsSpent(info))
        {
            var days = (int)Math.Floor((now - info.ExpiresAtUtc).TotalDays);
            message = $"Licenza scaduta il {info.ExpiresAtUtc.ToLocalTime().AddSeconds(-1):dd/MM/yyyy}" +
                      (days > 0 ? $" ({days} giorni fa)" : "") +
                      $". Per rinnovare scrivimi su Discord: {LicenseOptions.DiscordContact}";
            return LicenseState.Expired;
        }

        var left = info.DaysLeft(now);
        message = left <= LicenseOptions.RenewalWarningDays
            ? $"Licenza attiva, ma scade fra {left} giorn{(left == 1 ? "o" : "i")} " +
              $"({info.ExpiresAtUtc.ToLocalTime().AddSeconds(-1):dd/MM/yyyy}). " +
              $"Rinnova su Discord: {LicenseOptions.DiscordContact}"
            : $"Licenza attiva fino al {info.ExpiresAtUtc.ToLocalTime().AddSeconds(-1):dd/MM/yyyy} " +
              $"({left} giorni residui).";

        return LicenseState.Valid;
    }

    private static string BuildNotConfiguredMessage() =>
        "Questa build non ha ancora la chiave pubblica di firma: nessuna licenza puo' essere " +
        "verificata. Esegui 'keygen init' e incolla la chiave in LicenseOptions.PublicKey.";
}
