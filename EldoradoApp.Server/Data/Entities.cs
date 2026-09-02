using EldoradoApp.Contracts;

namespace EldoradoApp.Server.Data;

public sealed class CustomerAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Email { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<LicenseEntitlement> Licenses { get; set; } = [];
    public List<OrderRecord> Orders { get; set; } = [];
}

/// <summary>A server-side projection of one signed, offline licence key.</summary>
public sealed class LicenseEntitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string KeyId { get; set; } = "";
    public string KeyDigest { get; set; } = "";
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool IsDeviceLocked { get; set; }
    public string? DeviceTagDigest { get; set; }
    public EntitlementState State { get; set; } = EntitlementState.Active;
    public bool AutomationAllowed { get; set; } = true;
    public bool MessagingAllowed { get; set; } = true;
    public int? MaxMessagesPerHour { get; set; }
    public string? Note { get; set; }
    public Guid? CustomerId { get; set; }
    public CustomerAccount? Customer { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<DeviceActivation> Activations { get; set; } = [];
    public List<Subscription> Subscriptions { get; set; } = [];
    public List<OrderRecord> Orders { get; set; } = [];
}

public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LicenseId { get; set; }
    public LicenseEntitlement License { get; set; } = null!;
    public SubscriptionState State { get; set; } = SubscriptionState.Active;
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    public string? ExternalReference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A machine that has exchanged a valid licence for a short-lived API token.</summary>
public sealed class DeviceActivation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LicenseId { get; set; }
    public LicenseEntitlement License { get; set; } = null!;
    public string MachineDigest { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string TokenDigest { get; set; } = "";
    public string TokenPrefix { get; set; } = "";
    public DateTimeOffset TokenExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public BotConfiguration? Configuration { get; set; }
    public List<AutomationAuditEvent> AuditEvents { get; set; } = [];
}

/// <summary>Configuration backup for a device. It intentionally excludes any login credential.</summary>
public sealed class BotConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceActivationId { get; set; }
    public DeviceActivation DeviceActivation { get; set; } = null!;
    public string ConfigurationJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OrderRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExternalOrderId { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public CustomerAccount? Customer { get; set; }
    public Guid? LicenseId { get; set; }
    public LicenseEntitlement? License { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "pending";
    public string? Note { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AutomationAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceActivationId { get; set; }
    public DeviceActivation DeviceActivation { get; set; } = null!;
    public string Kind { get; set; } = "";
    public string? RequestId { get; set; }
    public string? BuyerId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Operator-wide kill switches, evaluated together with each individual licence.</summary>
public sealed class ServerPolicy
{
    public const string DefaultId = "default";

    public string Id { get; set; } = DefaultId;
    public bool AutomationAllowed { get; set; } = true;
    public bool MessagingAllowed { get; set; } = true;
    public int? MaxMessagesPerHour { get; set; }
    public string? MinimumClientVersion { get; set; }
    public string Message { get; set; } = "Servizio attivo";
    public DateTimeOffset ChangedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
