namespace EldoradoApp.Models;

/// <summary>Why the app is — or isn't — unlocked. Anything but <see cref="Valid"/> keeps the bot locked.</summary>
public enum LicenseState
{
    /// <summary>No key has ever been entered on this machine.</summary>
    None,

    /// <summary>Signature, device and dates all check out.</summary>
    Valid,

    /// <summary>Genuine key, but its last day has passed: the customer has to renew.</summary>
    Expired,

    /// <summary>Genuine key issued for a different PC (a shared or resold key).</summary>
    WrongDevice,

    /// <summary>Genuine key that was cancelled after it was sold (chargeback, abuse).</summary>
    Revoked,

    /// <summary>The system clock went backwards since the last run — an expiry dodge.</summary>
    ClockTampered,

    /// <summary>Not a key we issued: wrong length, mistyped, or forged signature.</summary>
    Malformed
}

/// <summary>
/// What a license key carries once decoded. Everything here is signed, so none of it can
/// be edited by the customer: the payload travels inside the key itself and there is no
/// server to ask.
/// </summary>
/// <param name="KeyId">Short public identifier (8 chars) — the one thing to quote in support and in the revocation list.</param>
/// <param name="Issued">Day the key was generated.</param>
/// <param name="Expires">Last day the key works; it dies at the end of this day, UTC.</param>
/// <param name="DeviceTag">6 bytes of the buyer's machine fingerprint, or empty for a floating key.</param>
public sealed record LicenseInfo(string KeyId, DateOnly Issued, DateOnly Expires, byte[] DeviceTag)
{
    /// <summary>False for a "floating" key, which runs on any PC (use it for yourself, or for a refund).</summary>
    public bool IsDeviceLocked => DeviceTag.Length > 0;

    /// <summary>The instant the key stops working: midnight at the end of <see cref="Expires"/>, UTC.</summary>
    public DateTimeOffset ExpiresAtUtc =>
        new(Expires.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    /// <summary>Whole days left before <see cref="ExpiresAtUtc"/>; 0 once it has run out.</summary>
    public int DaysLeft(DateTimeOffset nowUtc) =>
        (int)Math.Max(0, Math.Ceiling((ExpiresAtUtc - nowUtc).TotalDays));

    /// <summary>How long the key was sold for, in days.</summary>
    public int DurationDays => Expires.DayNumber - Issued.DayNumber + 1;
}
