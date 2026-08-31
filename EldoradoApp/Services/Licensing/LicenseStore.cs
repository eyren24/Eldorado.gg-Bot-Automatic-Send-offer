using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EldoradoApp.Services.Licensing;

/// <summary>The activated key as it sits on disk.</summary>
/// <remarks>
/// Deliberately thin: everything that decides whether the key is still good is either
/// signed inside the key itself or held by <see cref="TamperGuard"/>, which lives
/// elsewhere on purpose. Deleting this file loses the licence, not the expiry history.
/// </remarks>
public sealed class StoredLicense
{
    /// <summary>The key exactly as the customer pasted it.</summary>
    public string Key { get; set; } = "";

    /// <summary>When this PC first accepted the key.</summary>
    public DateTimeOffset ActivatedUtc { get; set; }
}

/// <summary>
/// Keeps the activated key under %AppData%\EldoradoApp, encrypted with Windows DPAPI in the
/// same way as <see cref="CredentialStore"/>. The encryption is not what makes the licence
/// safe - the signature does that - it just stops the file being copied to another PC or
/// hand-edited to move the expiry.
/// </summary>
public static class LicenseStore
{
    private static readonly string Directory_ =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp");

    private static readonly string FilePath = Path.Combine(Directory_, "license.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EldoradoApp.License.v1");

    public static bool Exists => File.Exists(FilePath);

    public static void Save(StoredLicense license)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            var plain = JsonSerializer.SerializeToUtf8Bytes(license);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
            Array.Clear(plain);
        }
        catch (Exception ex)
        {
            ApiLog.Write($"License not persisted: {ex.Message}");
        }
    }

    public static StoredLicense? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<StoredLicense>(plain);
            Array.Clear(plain);

            return string.IsNullOrWhiteSpace(stored?.Key) ? null : stored;
        }
        catch
        {
            // Copied from another PC, another Windows user, or simply corrupt: DPAPI refuses
            // to unprotect it. Treated as "no licence", which sends the buyer to activation.
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            ApiLog.Write($"License not removed: {ex.Message}");
        }
    }
}
