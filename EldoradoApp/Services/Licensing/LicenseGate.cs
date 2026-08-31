namespace EldoradoApp.Services.Licensing;

/// <summary>
/// A second, independent opinion on whether this copy is licensed, used by the parts of
/// the app that actually earn money - the polling loop and the offer submission.
/// </summary>
/// <remarks>
/// <para>
/// It deliberately does <b>not</b> ask <see cref="LicenseService"/>. It re-reads the
/// stored key and re-checks the signature, the machine and the dates from scratch, so
/// neutralising the licence means patching two unrelated pieces of code rather than
/// flipping one boolean. The duplication is the feature.
/// </para>
/// <para>
/// Honest limits: this is a client-side check in managed code. Somebody who can decompile
/// the assembly, patch it and repack the single-file bundle can defeat any number of
/// checks. The point is to make that a real reversing job rather than something a copied
/// script does, and to make casual sharing - the same key on a friend's PC, or a wound-back
/// clock - simply not work.
/// </para>
/// </remarks>
public static class LicenseGate
{
    /// <summary>Full re-verification, from the file on disk to the signature. Never throws.</summary>
    public static bool IsLicensed()
    {
        try
        {
            if (!LicenseOptions.IsConfigured || LicenseStore.Load() is not { } stored)
            {
                return false;
            }

            if (!LicenseCodec.TryDecode(stored.Key, out var payload, out var signature, out _) ||
                !LicenseCodec.Verify(payload, signature, LicenseOptions.PublicKey))
            {
                return false;
            }

            var info = LicenseCodec.Read(payload);
            var now = DateTimeOffset.UtcNow;

            return HardwareId.Matches(info.DeviceTag)
                   && !RevocationList.IsRevoked(info.KeyId)
                   && !TamperGuard.PredatesIssue(info, now)
                   && !TamperGuard.IsSpent(info)
                   && now < info.ExpiresAtUtc;
        }
        catch
        {
            // Anything unexpected counts as "not licensed": this gate fails closed.
            return false;
        }
    }

    /// <summary>What to tell the seller when <see cref="IsLicensed"/> said no.</summary>
    public const string Refusal =
        "Licenza non valida o scaduta: il bot non invia offerte. " +
        "Apri la scheda «Licenza» per rinnovare.";
}
