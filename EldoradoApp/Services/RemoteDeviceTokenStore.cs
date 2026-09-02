using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EldoradoApp.Services;

/// <summary>Stores only the server-issued device token, protected for this Windows user with DPAPI.</summary>
public static class RemoteDeviceTokenStore
{
    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldoradoApp");
    private static readonly string FilePath = Path.Combine(Directory_, "remote-device-token.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EldoradoApp.RemoteDeviceToken.v1");

    public static void Save(RemoteDeviceToken token)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            var plain = JsonSerializer.SerializeToUtf8Bytes(token);
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, protectedBytes);
            Array.Clear(plain);
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Remote device token not persisted: {ex.Message}");
        }
    }

    public static RemoteDeviceToken? Load(string? baseUrl)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var token = JsonSerializer.Deserialize<RemoteDeviceToken>(plain);
            Array.Clear(plain);

            return token is { Token.Length: > 0 } &&
                   string.Equals(token.BaseUrl.TrimEnd('/'), (baseUrl ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? token
                : null;
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
        catch (Exception ex)
        {
            ApiLog.Write($"Remote device token not removed: {ex.Message}");
        }
    }
}

public sealed record RemoteDeviceToken(string BaseUrl, string Token, string KeyId, DateTimeOffset ReceivedAtUtc);
