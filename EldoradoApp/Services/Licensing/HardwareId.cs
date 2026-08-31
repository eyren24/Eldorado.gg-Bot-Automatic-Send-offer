using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace EldoradoApp.Services.Licensing;

/// <summary>
/// A fingerprint of the PC, so a key sold to one buyer does not also unlock their friends'
/// machines. The buyer reads <see cref="Display"/> off the activation screen and sends it
/// over Discord; the key is then minted against it.
/// </summary>
/// <remarks>
/// <para>
/// Five independent sources are hashed together, on purpose. A single one - Windows'
/// <c>MachineGuid</c> - would be a one-line <c>regedit</c> away from being spoofed by
/// anyone who talked a paying customer into sharing theirs. Faking all five means faking
/// the CPU and the motherboard too.
/// </para>
/// <para>
/// Two of them (<c>HKLM\HARDWARE\…</c>) matter more than their weight suggests: that hive
/// is <i>volatile</i>. Windows rebuilds it from the firmware at every boot, so an edit
/// there needs admin rights, does not survive a restart, and cannot simply be exported and
/// imported on another PC like an ordinary registry key.
/// </para>
/// <para>
/// The trade-off is deliberate: replacing the motherboard, reformatting the system disk or
/// reinstalling Windows changes the ID and needs a new key. That is a two-minute Discord
/// message, and it is exactly the property that stops one key from covering a whole group.
/// </para>
/// </remarks>
public static class HardwareId
{
    private static readonly Lazy<byte[]> Fingerprint_ = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Raw hash of the machine. Its first bytes are what gets signed into a key.</summary>
    public static byte[] Fingerprint => Fingerprint_.Value;

    /// <summary>What the buyer copies and sends over: <c>XXXX-XXXX-XXXX-XXXX</c>.</summary>
    public static string Display => LicenseCodec.FormatMachineId(Fingerprint);

    /// <summary>The 6 bytes a device-locked key carries, for comparison against a key.</summary>
    public static ReadOnlySpan<byte> DeviceTag => Fingerprint.AsSpan(0, LicenseCodec.DeviceTagLength);

    /// <summary>True when the key was minted for this very PC (or is a floating key).</summary>
    public static bool Matches(ReadOnlySpan<byte> deviceTag) =>
        deviceTag.Length == 0 || CryptographicOperations.FixedTimeEquals(deviceTag, DeviceTag);

    /// <summary>
    /// Which sources answered, for support. Never shown to buyers - it would tell an
    /// attacker exactly which values to forge.
    /// </summary>
    public static string Diagnostics()
    {
        var sb = new StringBuilder();

        foreach (var (name, value) in Sources())
        {
            sb.AppendLine($"  {name,-14} {(value.Length > 0 ? $"ok ({value.Length} char)" : "assente")}");
        }

        return sb.ToString();
    }

    private static byte[] Compute()
    {
        // Namespaced so the same PC yields a different id in any other product that
        // happens to hash the same sources.
        var seed = new StringBuilder("EldoradoApp.HWID.v2");

        foreach (var (name, value) in Sources())
        {
            // The source name is part of the hash: a missing source and an empty one are
            // then still distinguishable from the sources shifting position.
            seed.Append('|').Append(name).Append('=').Append(value);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));
    }

    /// <summary>
    /// The five signals, in a fixed order. Every reader swallows its own errors and returns
    /// an empty string, so a machine missing one source still gets a stable ID rather than
    /// one that changes from run to run.
    /// </summary>
    private static IEnumerable<(string Name, string Value)> Sources()
    {
        // 1. Written once when Windows is installed; survives every hardware change.
        yield return ("machineguid", ReadHklm(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid"));

        // 2. Identity of the Windows installation itself.
        yield return ("windows",
            ReadHklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductId") + "/" +
            ReadHklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallDate"));

        // 3. Assigned by the filesystem when the system volume was formatted.
        yield return ("volume", ReadSystemVolumeSerial());

        // 4-5. Volatile hive: rebuilt from firmware at every boot, so not persistently editable.
        yield return ("cpu",
            ReadHklm(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") + "/" +
            ReadHklm(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "Identifier"));

        yield return ("board",
            ReadHklm(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer") + "/" +
            ReadHklm(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName") + "/" +
            ReadHklm(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct"));
    }

    private static string ReadHklm(string subKey, string name)
    {
        try
        {
            // Registry64 explicitly: a 32-bit process would otherwise be redirected to the
            // WOW6432Node view, where several of these values do not exist.
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: false);

            return key?.GetValue(name)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadSystemVolumeSerial()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            if (string.IsNullOrEmpty(root))
            {
                return "";
            }

            return GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0)
                ? serial.ToString("X8")
                : "";
        }
        catch
        {
            return "";
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}
