using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EldoradoApp.Models;

namespace EldoradoApp.Services.Licensing;

/// <summary>
/// The key format itself, shared byte-for-byte between the app and the offline generator
/// (the generator compiles this very file - see <c>tools/EldoradoKeygen</c>), so the two
/// can never drift apart.
/// </summary>
/// <remarks>
/// A key is a signed blob, not a lookup token: everything the app needs in order to decide
/// is inside it, which is why no licence server or database has to exist. Only the private
/// half of the key pair can mint one, and that half never ships with the app.
/// <code>
/// payload   21 B : [0] version | [1] flags | [2..6] key id | [7..10] issued
///                  [11..14] expiry | [15..20] device tag
/// signature 64 B : ECDSA P-256 / SHA-256, raw r+s
/// blob      85 B -> 136 Crockford base32 chars -> ELDO-XXXXXXXX-... (17 groups)
/// </code>
/// </remarks>
public static class LicenseCodec
{
    /// <summary>Human prefix on every printed key. Stripped before decoding.</summary>
    public const string Prefix = "ELDO";

    /// <summary>
    /// What the prefix looks like after normalisation - "E1D0", because the Crockford
    /// alphabet folds L onto 1 and O onto 0. Comparing against the raw prefix would never
    /// match, and every pasted key would be rejected as too long.
    /// </summary>
    private static readonly string NormalizedPrefix = Base32.Normalize(Prefix);

    private const byte CurrentVersion = 1;
    private const byte FlagDeviceLocked = 0b0000_0001;

    private const int PayloadLength = 21;
    private const int SignatureLength = 64;

    /// <summary>Bytes on the wire: payload followed by signature.</summary>
    public const int BlobLength = PayloadLength + SignatureLength;

    /// <summary>Characters a printed key decodes to, once dashes and the prefix are gone.</summary>
    public const int TextLength = 136;   // 85 bytes * 8 / 5

    private const int KeyIdLength = 5;

    /// <summary>Bytes of the machine fingerprint baked into a locked key.</summary>
    public const int DeviceTagLength = 6;

    /// <summary>Bytes of the fingerprint shown to the customer as their machine ID.</summary>
    public const int MachineIdLength = 10;

    /// <summary>Characters in a printed machine ID, separators excluded.</summary>
    public const int MachineIdTextLength = 16;   // 10 bytes * 8 / 5

    /// <summary>A fresh public identifier for a key - 8 printable characters.</summary>
    public static string NewKeyId() => Base32.Encode(RandomNumberGenerator.GetBytes(KeyIdLength));

    /// <summary>Renders a fingerprint the way the customer reads it off the screen.</summary>
    public static string FormatMachineId(ReadOnlySpan<byte> fingerprint) =>
        Group(Base32.Encode(fingerprint[..MachineIdLength]), 4);

    /// <summary>
    /// Turns a machine ID pasted by the customer back into the tag that gets signed into
    /// their key. Both sides derive the tag from the same prefix of the same hash, so the
    /// generator never needs the raw fingerprint - the 16 printed characters are enough.
    /// </summary>
    public static bool TryParseMachineId(string? text, out byte[] deviceTag)
    {
        deviceTag = [];

        var normalized = Base32.Normalize(text ?? "");
        if (normalized.Length != MachineIdTextLength ||
            !Base32.TryDecode(normalized, MachineIdLength, out var fingerprint))
        {
            return false;
        }

        deviceTag = fingerprint[..DeviceTagLength];
        return true;
    }

    /// <summary>Lays out the signable bytes. Pass an empty tag for a key that runs on any PC.</summary>
    public static byte[] BuildPayload(string keyId, DateOnly issued, DateOnly expires, ReadOnlySpan<byte> deviceTag)
    {
        if (!Base32.TryDecode(Base32.Normalize(keyId), KeyIdLength, out var id))
        {
            throw new ArgumentException($"Key id non valido: '{keyId}'.", nameof(keyId));
        }

        if (deviceTag.Length is not (0 or DeviceTagLength))
        {
            throw new ArgumentException("Il device tag deve essere vuoto oppure di 6 byte.", nameof(deviceTag));
        }

        if (expires < issued)
        {
            throw new ArgumentException("La scadenza precede l'emissione.", nameof(expires));
        }

        var payload = new byte[PayloadLength];
        payload[0] = CurrentVersion;
        payload[1] = deviceTag.Length > 0 ? FlagDeviceLocked : (byte)0;
        id.CopyTo(payload.AsSpan(2, KeyIdLength));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(7, 4), (uint)issued.DayNumber);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(11, 4), (uint)expires.DayNumber);
        deviceTag.CopyTo(payload.AsSpan(15, DeviceTagLength));

        return payload;
    }

    /// <summary>Reads back what <see cref="BuildPayload"/> wrote.</summary>
    public static LicenseInfo Read(ReadOnlySpan<byte> payload)
    {
        var locked = (payload[1] & FlagDeviceLocked) != 0;

        return new LicenseInfo(
            Base32.Encode(payload.Slice(2, KeyIdLength)),
            DateOnly.FromDayNumber((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(7, 4))),
            DateOnly.FromDayNumber((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(11, 4))),
            locked ? payload.Slice(15, DeviceTagLength).ToArray() : []);
    }

    /// <summary>The printed key: the prefix plus 17 groups of 8.</summary>
    public static string Format(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        Span<byte> blob = stackalloc byte[BlobLength];
        payload.CopyTo(blob);
        signature.CopyTo(blob[PayloadLength..]);

        return Prefix + "-" + Group(Base32.Encode(blob), 8);
    }

    /// <summary>
    /// Parses whatever the customer pasted - with or without the prefix, dashes, spaces or
    /// line breaks - into its two halves. <paramref name="error"/> is customer-facing Italian.
    /// </summary>
    public static bool TryDecode(string? text, out byte[] payload, out byte[] signature, out string error)
    {
        payload = [];
        signature = [];

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Incolla la chiave che ti e' stata consegnata.";
            return false;
        }

        var normalized = Base32.Normalize(text);

        // The blob always starts with version byte 1, whose first five bits are 00000 - so a
        // real key always begins with '0' and never with the prefix. Stripping one leading
        // prefix is therefore unambiguous.
        if (normalized.StartsWith(NormalizedPrefix, StringComparison.Ordinal))
        {
            normalized = normalized[NormalizedPrefix.Length..];
        }

        if (normalized.Length != TextLength)
        {
            error = normalized.Length < TextLength
                ? "Chiave incompleta: nella copia manca un pezzo."
                : "Chiave troppo lunga: copia soltanto la riga della chiave.";
            return false;
        }

        if (!Base32.TryDecode(normalized, BlobLength, out var blob))
        {
            error = "Chiave non valida: contiene caratteri che una chiave non puo' avere.";
            return false;
        }

        if (blob[0] != CurrentVersion)
        {
            error = $"Chiave di formato v{blob[0]}: aggiorna l'app all'ultima versione.";
            return false;
        }

        payload = blob[..PayloadLength];
        signature = blob[PayloadLength..];
        error = "";
        return true;
    }

    /// <summary>True when the signature really was produced by the matching private key.</summary>
    public static bool Verify(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature, string publicKeyBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);

            return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            // A malformed public key or signature is simply "not one of ours".
            return false;
        }
    }

    /// <summary>Signs a payload. Only the generator ever holds a private key to pass in here.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> payload, ECDsa privateKey) =>
        privateKey.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>
    /// The bytes a revocation list is signed over: the key ids, uppercased, de-duplicated,
    /// sorted and joined by commas. Sorting lets the file be regenerated in any order and
    /// still verify.
    /// </summary>
    public static byte[] RevocationDigest(IEnumerable<string> keyIds) =>
        Encoding.UTF8.GetBytes(string.Join(',', keyIds
            .Select(id => id.Trim().ToUpperInvariant())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)));

    private static string Group(string text, int size)
    {
        var sb = new StringBuilder(text.Length + text.Length / size);

        for (var i = 0; i < text.Length; i += size)
        {
            if (i > 0)
            {
                sb.Append('-');
            }

            sb.Append(text.AsSpan(i, Math.Min(size, text.Length - i)));
        }

        return sb.ToString();
    }
}
