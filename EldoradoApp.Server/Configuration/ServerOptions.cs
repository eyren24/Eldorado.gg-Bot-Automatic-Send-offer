namespace EldoradoApp.Server.Configuration;

/// <summary>Values that make the API recognise the same signed licences as the desktop app.</summary>
public sealed class LicensingOptions
{
    public const string SectionName = "Licensing";

    /// <summary>ECDSA public key produced by the existing key generator; never a private key.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>How long a device-scoped server token remains valid before the client activates again.</summary>
    public int DeviceTokenDays { get; set; } = 30;

    /// <summary>Floating licences may deliberately be limited even though their local key has no PC tag.</summary>
    public int MaxDevicesForFloatingLicense { get; set; } = 1;

    /// <summary>Short grace period the desktop client can use after a successful server check.</summary>
    public int OfflineGraceMinutes { get; set; } = 60;
}

/// <summary>One shared operator key protects administrative endpoints until a real identity provider is added.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    public string ApiKey { get; set; } = "";
}

/// <summary>Database bootstrapping is enabled for a fresh local or Docker installation.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public bool AutoCreate { get; set; } = true;
}
