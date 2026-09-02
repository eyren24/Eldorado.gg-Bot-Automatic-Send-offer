using System.Security.Cryptography;
using System.Text;

namespace EldoradoApp.Server.Services;

internal static class Hashing
{
    public static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
