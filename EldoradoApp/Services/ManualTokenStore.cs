using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EldoradoApp.Services;

/// <summary>
/// Persists the hand-pasted IdToken encrypted with Windows DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>) under %AppData%\EldoradoApp, so a
/// short app restart within the token's lifetime keeps the session.
/// </summary>
public static class ManualTokenStore
{
    private static readonly string Directory_ =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp");

    private static readonly string FilePath = Path.Combine(Directory_, "eldorado.idtoken");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EldoradoApp.ManualToken.v1");

    public static bool Exists => File.Exists(FilePath);

    public static void Save(string token)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
        }
        catch
        {
            // Non-fatal: just won't persist.
        }
    }

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
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
