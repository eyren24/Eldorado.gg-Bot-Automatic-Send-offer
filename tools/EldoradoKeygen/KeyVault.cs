using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EldoradoKeygen;

/// <summary>The encrypted private key as it sits on disk.</summary>
public sealed class VaultFile
{
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("iterations")] public int Iterations { get; set; }
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
}

/// <summary>
/// Holds the one secret in the whole system: the private half of the signing pair. Losing
/// it means never being able to issue another key for the builds already in customers'
/// hands; leaking it means anyone can mint keys for free. Hence a passphrase rather than
/// DPAPI - this file has to survive a Windows reinstall and be safe to keep in a backup.
/// </summary>
public static class KeyVault
{
    private const int Iterations = 210_000;   // OWASP guidance for PBKDF2-HMAC-SHA256

    public static void Save(string path, ECDsa key, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = key.ExportPkcs8PrivateKey();
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using (var aes = new AesGcm(Derive(passphrase, salt), tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        CryptographicOperations.ZeroMemory(plain);

        var vault = new VaultFile
        {
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Key = Convert.ToBase64String(cipher),
            Iterations = Iterations,
            Created = DateTimeOffset.UtcNow
        };

        File.WriteAllText(path, JsonSerializer.Serialize(vault, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Opens the vault, or throws with a message worth showing the operator.</summary>
    public static ECDsa Load(string path, string passphrase)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Nessuna chiave privata in {path}. Esegui prima 'keygen init'.", path);
        }

        var vault = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path))
                    ?? throw new InvalidOperationException($"File chiave illeggibile: {path}");

        var plain = new byte[Convert.FromBase64String(vault.Key).Length];

        try
        {
            using var aes = new AesGcm(
                Derive(passphrase, Convert.FromBase64String(vault.Salt), vault.Iterations),
                Convert.FromBase64String(vault.Tag).Length);

            aes.Decrypt(
                Convert.FromBase64String(vault.Nonce),
                Convert.FromBase64String(vault.Key),
                Convert.FromBase64String(vault.Tag),
                plain);
        }
        catch (CryptographicException)
        {
            // AES-GCM authenticates: a bad tag means a wrong passphrase or a tampered file.
            throw new InvalidOperationException("Passphrase errata (oppure il file della chiave e' danneggiato).");
        }

        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(plain, out _);
        CryptographicOperations.ZeroMemory(plain);

        return ecdsa;
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations = Iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);
}
