using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EldoradoApp.Models;
using Microsoft.Win32;

namespace EldoradoApp.Services.Licensing;

/// <summary>What the guard remembers between runs.</summary>
public sealed class TamperState
{
    /// <summary>The furthest point in time the app has ever legitimately observed.</summary>
    public DateTimeOffset MaxSeenUtc { get; set; }

    /// <summary>Key ids the app has already watched run out. They never come back.</summary>
    public List<string> Burned { get; set; } = [];
}

/// <summary>
/// Closes the obvious way around an expiry date: delete the licence, wind the Windows
/// clock back, re-paste the same old key. The expiry lives inside the signed payload, so
/// without a memory of its own the app would happily believe the clock.
/// </summary>
/// <remarks>
/// <para>
/// The memory is kept in two unrelated places - a registry value under HKCU and a file
/// under <c>%LocalAppData%</c>, neither next to <c>license.bin</c> - and every read merges
/// both and writes the result back to both. Deleting one restores it from the other; the
/// attacker has to find and clear both, in the same run, and roll the clock back.
/// </para>
/// <para>
/// Both copies are DPAPI-encrypted for the current user, so they cannot be hand-edited or
/// carried to another PC, and both are treated as advisory: a corrupt or missing store
/// degrades to "no history", never to a locked-out paying customer.
/// </para>
/// </remarks>
public static class TamperGuard
{
    private const string RegistryPath = @"Software\EldoradoApp";
    private const string RegistryValue = "State";
    private const int MaxBurned = 64;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldoradoApp", "state.bin");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EldoradoApp.Guard.v1");

    /// <summary>A clock reading this far past a key's end is a broken clock, not elapsed time.</summary>
    private static readonly TimeSpan ImplausiblyLate = TimeSpan.FromDays(365);

    /// <summary>
    /// How far the mark must move before it is worth rewriting both copies. The app
    /// re-checks every few minutes and can stay open for days; half-hour resolution is
    /// plenty for catching a rollback and keeps the writes down to a couple of dozen a day.
    /// </summary>
    private static readonly TimeSpan Granularity = TimeSpan.FromMinutes(30);

    private static TamperState? _state;

    private static TamperState State => _state ??= Merge(ReadRegistry(), ReadFile());

    /// <summary>
    /// True when the app has already watched this key run out - whatever the clock says
    /// now. Survives deleting the licence and re-pasting the same key.
    /// </summary>
    public static bool IsSpent(LicenseInfo info) =>
        State.Burned.Contains(info.KeyId, StringComparer.Ordinal) ||
        State.MaxSeenUtc >= info.ExpiresAtUtc;

    /// <summary>
    /// True when the clock reads earlier than the day the key was issued - it cannot
    /// legitimately be in use before it existed.
    /// </summary>
    public static bool PredatesIssue(LicenseInfo info, DateTimeOffset nowUtc) =>
        nowUtc < new DateTimeOffset(info.Issued.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    /// <summary>
    /// Records what the clock says, ignoring readings too far out to be real. The mark is
    /// capped at the key's own expiry, so a clock running fast can never push it beyond the
    /// point the licence was going to end anyway.
    /// </summary>
    public static void Observe(LicenseInfo info, DateTimeOffset nowUtc)
    {
        if (PredatesIssue(info, nowUtc) || nowUtc > info.ExpiresAtUtc + ImplausiblyLate)
        {
            return;   // the clock is nonsense; remembering it would poison the guard
        }

        var state = State;
        var changed = false;

        var mark = nowUtc < info.ExpiresAtUtc ? nowUtc : info.ExpiresAtUtc;

        // The expiry itself is always worth recording exactly; anything short of it only
        // when it has moved far enough to matter.
        if (mark > state.MaxSeenUtc && (mark >= info.ExpiresAtUtc || mark - state.MaxSeenUtc >= Granularity))
        {
            state.MaxSeenUtc = mark;
            changed = true;
        }

        if (nowUtc >= info.ExpiresAtUtc && !state.Burned.Contains(info.KeyId, StringComparer.Ordinal))
        {
            state.Burned.Add(info.KeyId);
            if (state.Burned.Count > MaxBurned)
            {
                state.Burned.RemoveRange(0, state.Burned.Count - MaxBurned);
            }

            changed = true;
        }

        if (changed)
        {
            Save(state);
        }
    }

    /// <summary>
    /// Lets a genuine renewal clear a guard that fired on a badly wrong system clock. Only
    /// a key that is signed, unburned and outlives everything seen so far qualifies - which
    /// means only a key the seller actually issued can reset the mark.
    /// </summary>
    public static void AcceptRenewal(LicenseInfo info, DateTimeOffset nowUtc)
    {
        var state = State;

        if (state.MaxSeenUtc <= nowUtc || info.ExpiresAtUtc <= state.MaxSeenUtc)
        {
            return;
        }

        state.MaxSeenUtc = nowUtc;
        Save(state);
    }

    /// <summary>
    /// Wipes the memory. Deliberately NOT wired to any button - "rimuovi licenza" must not
    /// be a one-click way to clear the expiry history. It exists for support and for the
    /// test harness.
    /// </summary>
    public static void Reset()
    {
        _state = new TamperState();
        Save(_state);
    }

    // ---- storage: two copies, either one is enough to rebuild the other ----

    private static void Save(TamperState state)
    {
        byte[] blob;

        try
        {
            blob = ProtectedData.Protect(
                JsonSerializer.SerializeToUtf8Bytes(state), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Guard not encrypted: {ex.Message}");
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue(RegistryValue, Convert.ToBase64String(blob), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Guard registry copy not written: {ex.Message}");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, blob);
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Guard file copy not written: {ex.Message}");
        }
    }

    private static TamperState? ReadRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(RegistryValue) is string text ? Decode(Convert.FromBase64String(text)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static TamperState? ReadFile()
    {
        try
        {
            return File.Exists(FilePath) ? Decode(File.ReadAllBytes(FilePath)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static TamperState? Decode(byte[] blob)
    {
        try
        {
            return JsonSerializer.Deserialize<TamperState>(
                ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // Tampered with, or written by another Windows user: treated as absent.
            return null;
        }
    }

    /// <summary>
    /// Takes the strictest of the two copies and, if they disagreed, writes the result back
    /// so a deleted copy is restored on the very next run.
    /// </summary>
    private static TamperState Merge(TamperState? a, TamperState? b)
    {
        if (a is null && b is null)
        {
            return new TamperState();
        }

        var merged = new TamperState
        {
            MaxSeenUtc = Later(a?.MaxSeenUtc, b?.MaxSeenUtc),
            Burned = (a?.Burned ?? []).Concat(b?.Burned ?? [])
                .Distinct(StringComparer.Ordinal)
                .TakeLast(MaxBurned)
                .ToList()
        };

        if (a is null || b is null ||
            a.MaxSeenUtc != b.MaxSeenUtc ||
            a.Burned.Count != merged.Burned.Count ||
            b.Burned.Count != merged.Burned.Count)
        {
            Save(merged);
        }

        return merged;
    }

    private static DateTimeOffset Later(DateTimeOffset? a, DateTimeOffset? b) =>
        (a ?? default) > (b ?? default) ? a ?? default : b ?? default;
}
