using EldoradoApp.Contracts;

namespace EldoradoApp.Models;

/// <summary>Persisted, non-secret connection settings for the optional ASP.NET Core control plane.</summary>
public sealed class RemoteControlSettings
{
    /// <summary>Root URL of EldoradoApp.Server, for example https://bot.example.com.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// When true, the bot fails closed after the server grace period. Leave false while
    /// migrating or when deliberately running an offline-only licence installation.
    /// </summary>
    public bool RequireServer { get; set; }

    /// <summary>Whether a safe copy of bot settings is uploaded after an explicit Save.</summary>
    public bool SyncConfiguration { get; set; } = true;

    /// <summary>Last server verdict, cached only for the configured offline grace window.</summary>
    public RemoteEntitlementSnapshot? LastEntitlement { get; set; }

    /// <summary>Diagnostics only; never contains the device token or a user credential.</summary>
    public string LastError { get; set; } = "";

    public bool IsConfigured => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
                                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public void Normalize()
    {
        BaseUrl = (BaseUrl ?? "").Trim().TrimEnd('/');
        LastError ??= "";
    }
}

/// <summary>Compact cached form of the server response; it lets the runtime apply a bounded offline grace period.</summary>
public sealed class RemoteEntitlementSnapshot
{
    public EntitlementState State { get; set; } = EntitlementState.Unknown;
    public string KeyId { get; set; } = "";
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public bool AutomationAllowed { get; set; }
    public bool MessagingAllowed { get; set; }
    public int? MaxMessagesPerHour { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset CheckedAtUtc { get; set; }
    public DateTimeOffset? OfflineUntilUtc { get; set; }
    public string? MinimumClientVersion { get; set; }

    public static RemoteEntitlementSnapshot From(EldoradoApp.Contracts.EntitlementResponse response) => new()
    {
        State = response.State,
        KeyId = response.KeyId,
        ExpiresAtUtc = response.ExpiresAtUtc,
        AutomationAllowed = response.AutomationAllowed,
        MessagingAllowed = response.MessagingAllowed,
        MaxMessagesPerHour = response.MaxMessagesPerHour,
        Message = response.Message,
        CheckedAtUtc = response.CheckedAtUtc,
        OfflineUntilUtc = response.OfflineUntilUtc,
        MinimumClientVersion = response.MinimumClientVersion
    };
}
