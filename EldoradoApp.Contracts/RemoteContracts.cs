namespace EldoradoApp.Contracts;

/// <summary>How the server currently authorises a licensed desktop installation.</summary>
public enum EntitlementState
{
    Active,
    Expired,
    Revoked,
    Suspended,
    Unknown
}

/// <summary>Lifecycle state of a paid subscription associated with a licence.</summary>
public enum SubscriptionState
{
    Active,
    PastDue,
    Cancelled,
    Expired,
    Suspended
}

/// <summary>Request made once after a locally validated licence is pasted into the app.</summary>
public sealed record ActivateLicenseRequest(
    string LicenseKey,
    string MachineId,
    string DeviceName,
    string? AppVersion);

/// <summary>Server response to an activation or entitlement refresh.</summary>
public sealed record EntitlementResponse(
    EntitlementState State,
    string KeyId,
    DateTimeOffset? ExpiresAtUtc,
    bool AutomationAllowed,
    bool MessagingAllowed,
    int? MaxMessagesPerHour,
    string Message,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? OfflineUntilUtc = null,
    string? MinimumClientVersion = null);

/// <summary>Activation includes a device-scoped secret, never a reusable user credential.</summary>
public sealed record ActivateLicenseResponse(
    string DeviceToken,
    EntitlementResponse Entitlement);

/// <summary>Global controls delivered by the server to a desktop client.</summary>
public sealed record RemotePolicyResponse(
    bool AutomationAllowed,
    bool MessagingAllowed,
    int? MaxMessagesPerHour,
    string? MinimumClientVersion,
    string Message,
    DateTimeOffset ChangedAtUtc);

/// <summary>A safe, serialised copy of bot configuration; credentials are never included.</summary>
public sealed record BotConfigurationRequest(string ConfigurationJson, DateTimeOffset UpdatedAtUtc);

public sealed record BotConfigurationResponse(string ConfigurationJson, DateTimeOffset UpdatedAtUtc);

/// <summary>Audit event sent after an automated action; it gives the operator server-side traceability.</summary>
public sealed record AutomationAuditEventRequest(
    string Kind,
    string? RequestId,
    string? BuyerId,
    string? Detail,
    DateTimeOffset OccurredAtUtc);

/// <summary>Admin-only input for manually applying a server-side control to a licence.</summary>
public sealed record UpdateLicensePolicyRequest(
    bool? AutomationAllowed,
    bool? MessagingAllowed,
    int? MaxMessagesPerHour,
    EntitlementState? State,
    string? Note);

/// <summary>Admin-only order record. Payment processor data remains external.</summary>
public sealed record CreateOrderRequest(
    string ExternalOrderId,
    string? CustomerEmail,
    string? LicenseKeyId,
    decimal Amount,
    string Currency,
    string Status,
    string? Note);
