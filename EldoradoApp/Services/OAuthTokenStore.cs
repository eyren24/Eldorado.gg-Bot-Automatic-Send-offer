using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EldoradoApp.Services;

/// <summary>Persisted OAuth secrets for the Google (Hosted UI) sign-in.</summary>
public sealed record OAuthTokens(string RefreshToken);

/// <summary>
/// Stores the Google OAuth refresh token encrypted with Windows DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>) under %AppData%\EldoradoApp.
/// Mirrors <see cref="CredentialStore"/> but for the federated login path.
/// </summary>
public static class OAuthTokenStore
{
    private static readonly string Directory_ =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp");

    private static readonly string FilePath = Path.Combine(Directory_, "eldorado.oauth");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EldoradoApp.OAuth.v1");

    public static bool Exists => File.Exists(FilePath);

    public static void Save(OAuthTokens tokens)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
            Array.Clear(plain);
        }
        catch
        {
            // Non-fatal: just won't persist.
        }
    }

    public static OAuthTokens? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var tokens = JsonSerializer.Deserialize<OAuthTokens>(plain);
            Array.Clear(plain);
            return tokens;
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
