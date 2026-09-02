namespace EldoradoApp.Services;

/// <summary>
/// A narrow runtime bridge used by independent enforcement points. Keeping it separate
/// means the offer engine and messenger both honour a remote suspension without gaining
/// access to the server token itself.
/// </summary>
public static class RemoteControlGate
{
    private static Func<bool>? _automation;
    private static Func<bool>? _messaging;

    public static void Configure(Func<bool> automation, Func<bool> messaging)
    {
        Interlocked.Exchange(ref _automation, automation);
        Interlocked.Exchange(ref _messaging, messaging);
    }

    /// <summary>Unhooks the delegates so a shut-down service is not called by a stray timer.</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _automation, null);
        Interlocked.Exchange(ref _messaging, null);
    }

    /// <summary>
    /// Unconfigured means "allow". The remote control plane is opt-in, and the offline
    /// signed licence is still the real gate — see <c>LicenseGate.IsLicensed</c>, which
    /// checks the signature, the machine and the dates before ever asking this.
    /// </summary>
    public static bool AllowsAutomation() => Invoke(Volatile.Read(ref _automation));

    public static bool AllowsMessaging() => Invoke(Volatile.Read(ref _messaging));

    /// <summary>
    /// A throwing delegate must not take the bot down, and must not silently unlock it
    /// either: an exception here means the verdict is unknown, so the answer is "no".
    /// </summary>
    private static bool Invoke(Func<bool>? probe)
    {
        if (probe is null)
        {
            return true;
        }

        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Remote control gate probe failed: {ex.Message}");
            return false;
        }
    }
}
