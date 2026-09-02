using System.Security.Cryptography;
using EldoradoApp.Models;
using EldoradoApp.Server.Configuration;
using EldoradoApp.Services.Licensing;
using Microsoft.Extensions.Options;

namespace EldoradoApp.Server.Services;

public sealed record VerifiedLicense(
    LicenseInfo Info,
    string KeyDigest,
    string MachineDigest,
    string? DeviceTagDigest);

/// <summary>
/// Verifies the exact offline key format used by the existing desktop build. The API only
/// owns the public verification key; generating a licence remains an offline operation.
/// </summary>
public sealed class LicenseKeyVerifier(IOptions<LicensingOptions> options)
{
    public bool TryVerify(string? rawKey, string? machineId, out VerifiedLicense? verified, out string error)
    {
        verified = null;
        var publicKey = options.Value.PublicKey?.Trim() ?? "";

        if (publicKey.Length == 0)
        {
            error = "Il server non ha una chiave pubblica di licenza configurata.";
            return false;
        }

        if (!LicenseCodec.TryDecode(rawKey, out var payload, out var signature, out error) ||
            !LicenseCodec.Verify(payload, signature, publicKey))
        {
            error = error.Length == 0 ? "La firma della licenza non è valida." : error;
            return false;
        }

        var info = LicenseCodec.Read(payload);
        if (DateTimeOffset.UtcNow >= info.ExpiresAtUtc)
        {
            error = "Questa licenza è scaduta.";
            return false;
        }

        byte[] machineTag = [];
        if (!LicenseCodec.TryParseMachineId(machineId, out machineTag))
        {
            error = "L'ID macchina non è nel formato previsto.";
            return false;
        }

        if (info.IsDeviceLocked && !CryptographicOperations.FixedTimeEquals(info.DeviceTag, machineTag))
        {
            error = "Questa licenza è stata emessa per un altro PC.";
            return false;
        }

        var signedBlob = new byte[payload.Length + signature.Length];
        payload.CopyTo(signedBlob, 0);
        signature.CopyTo(signedBlob, payload.Length);

        verified = new VerifiedLicense(
            info,
            Hashing.Sha256Hex(signedBlob),
            Hashing.Sha256Hex(Base32.Normalize(machineId ?? "")),
            info.IsDeviceLocked ? Hashing.Sha256Hex(info.DeviceTag) : null);
        error = "";
        return true;
    }
}
