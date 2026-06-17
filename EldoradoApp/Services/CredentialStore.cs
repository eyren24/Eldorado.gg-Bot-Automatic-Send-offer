using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Stores the seller's Cognito credentials encrypted with Windows DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>) under %AppData%\EldoradoApp.
/// </summary>
public static class CredentialStore
{
    private static readonly string Directory_ =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp");

    private static readonly string FilePath = Path.Combine(Directory_, "eldorado.cred");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EldoradoApp.Cognito.v1");

    public static bool Exists => File.Exists(FilePath);

    public static void Save(EldoradoCredentials credentials)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
            Array.Clear(plain);
        }
        catch
        {
            // Non-fatal: just won't persist.
        }
    }

    public static EldoradoCredentials? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var creds = JsonSerializer.Deserialize<EldoradoCredentials>(plain);
            Array.Clear(plain);
            return creds;
        }
        catch
        {
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
        catch
        {
            // Non-fatal.
        }
    }
}
