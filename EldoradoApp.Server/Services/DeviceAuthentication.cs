using EldoradoApp.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace EldoradoApp.Server.Services;

public sealed record DeviceSession(DeviceActivation Activation, LicenseEntitlement License);

/// <summary>Resolves a device token to its licence and prevents tokens from crossing devices.</summary>
public sealed class DeviceAuthentication(EldoradoDbContext db, DeviceTokenService tokens)
{
    public async Task<DeviceSession?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue(DeviceTokenService.HeaderName, out var values))
        {
            return null;
        }

        var token = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var digest = tokens.Digest(token);
        var activation = await db.DeviceActivations
            .Include(x => x.Configuration)
            .Include(x => x.License)
                .ThenInclude(x => x.Subscriptions)
            .SingleOrDefaultAsync(x => x.TokenDigest == digest, cancellationToken);

        if (activation is null || activation.RevokedAtUtc is not null || activation.TokenExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        activation.LastSeenAtUtc = DateTimeOffset.UtcNow;
        return new DeviceSession(activation, activation.License);
    }
}
