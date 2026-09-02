using EldoradoApp.Contracts;
using EldoradoApp.Server.Configuration;
using EldoradoApp.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EldoradoApp.Server.Services;

/// <summary>Combines signed-key lifetime, paid subscription and operator switches into one verdict.</summary>
public sealed class EntitlementService(
    EldoradoDbContext db,
    IOptions<LicensingOptions> licensing)
{
    public async Task<ServerPolicy> GetPolicyAsync(CancellationToken cancellationToken)
    {
        var policy = await db.ServerPolicies.SingleOrDefaultAsync(x => x.Id == ServerPolicy.DefaultId, cancellationToken);
        if (policy is not null)
        {
            return policy;
        }

        policy = new ServerPolicy();
        db.ServerPolicies.Add(policy);
        await db.SaveChangesAsync(cancellationToken);
        return policy;
    }

    public async Task<EntitlementResponse> DescribeAsync(
        LicenseEntitlement license,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var policy = await GetPolicyAsync(cancellationToken);
        var state = license.State;
        var effectiveEnd = license.ExpiresAtUtc;

        if (state == EntitlementState.Active)
        {
            var activeSubscription = license.Subscriptions
                .Where(x => x.State == SubscriptionState.Active && x.StartsAtUtc <= now)
                .OrderByDescending(x => x.EndsAtUtc)
                .FirstOrDefault();

            if (activeSubscription is null || activeSubscription.EndsAtUtc <= now || license.ExpiresAtUtc <= now)
            {
                state = EntitlementState.Expired;
            }
            else
            {
                effectiveEnd = activeSubscription.EndsAtUtc < effectiveEnd
                    ? activeSubscription.EndsAtUtc
                    : effectiveEnd;
            }
        }

        var active = state == EntitlementState.Active && now < effectiveEnd;
        var automation = active && license.AutomationAllowed && policy.AutomationAllowed;
        var messaging = active && automation && license.MessagingAllowed && policy.MessagingAllowed;
        var maxMessages = Min(license.MaxMessagesPerHour, policy.MaxMessagesPerHour);
        var note = string.IsNullOrWhiteSpace(license.Note) ? policy.Message : license.Note!;

        return new EntitlementResponse(
            state,
            license.KeyId,
            effectiveEnd,
            automation,
            messaging,
            maxMessages,
            note,
            now,
            active ? now.AddMinutes(Math.Clamp(licensing.Value.OfflineGraceMinutes, 0, 24 * 60)) : null,
            policy.MinimumClientVersion);
    }

    public async Task<RemotePolicyResponse> DescribePolicyAsync(CancellationToken cancellationToken)
    {
        var policy = await GetPolicyAsync(cancellationToken);
        return new RemotePolicyResponse(
            policy.AutomationAllowed,
            policy.MessagingAllowed,
            policy.MaxMessagesPerHour,
            policy.MinimumClientVersion,
            policy.Message,
            policy.ChangedAtUtc);
    }

    private static int? Min(int? left, int? right) => left switch
    {
        null => right,
        _ when right is null => left,
        _ => Math.Min(left.Value, right.Value)
    };
}
