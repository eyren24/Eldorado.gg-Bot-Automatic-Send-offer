namespace EldoradoApp.Services.Licensing;

/// <summary>
/// The three things that turn the licence machinery into <i>your</i> licence machinery.
/// Everything else in <c>Services/Licensing</c> is generic and needs no editing.
/// </summary>
public static class LicenseOptions
{
    /// <summary>
    /// Public half of the signing key pair, base64 SubjectPublicKeyInfo, printed by
    /// <c>keygen init</c>. Safe to ship: it can only <i>check</i> keys, never mint them.
    /// While it is empty the app refuses every key, which is deliberate - a build that
    /// shipped without a key would otherwise accept anything.
    /// </summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWlHmrKsDQ6atW+olUwqdBZV+Ky5cMIz9SD0IUYjSUhCgpkFVV5oHRvC5blm+dbGxdal8ltwDGhwzKFzqyG3sOw==";

    /// <summary>Where buyers go to get or renew a key. Shown on the activation screen.</summary>
    public const string DiscordContact = "@eyren24";

    /// <summary>
    /// Optional invite link opened by the "Apri Discord" button. Leave empty to only show
    /// <see cref="DiscordContact"/> as text.
    /// </summary>
    public const string DiscordInvite = "";

    /// <summary>
    /// Optional URL of a signed <c>revoked.json</c> (a public gist works). Empty means keys
    /// can never be cancelled once sold - which is fine, and is what keeps this design
    /// server-free. Set it only if you want to be able to kill a key you already delivered.
    /// </summary>
    public const string RevocationListUrl = "";

    /// <summary>How long a fetched revocation list is trusted before the app re-fetches it.</summary>
    public static readonly TimeSpan RevocationRefreshInterval = TimeSpan.FromHours(6);

    /// <summary>The app nags about renewing once the licence gets this close to its end.</summary>
    public const int RenewalWarningDays = 5;

    /// <summary>True once a real signing key has been baked in.</summary>
    public static bool IsConfigured => PublicKey.Trim().Length > 0;
}
