using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using EldoradoApp.Contracts;
using EldoradoApp.Server.Configuration;
using EldoradoApp.Server.Data;
using EldoradoApp.Server.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LicensingOptions>(builder.Configuration.GetSection(LicensingOptions.SectionName));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("Eldorado")
                       ?? throw new InvalidOperationException("Imposta ConnectionStrings:Eldorado per usare PostgreSQL.");

builder.Services.AddDbContext<EldoradoDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<LicenseKeyVerifier>();
builder.Services.AddScoped<DeviceAuthentication>();
builder.Services.AddScoped<EntitlementService>();
builder.Services.AddSingleton<DeviceTokenService>();
builder.Services.AddSingleton<AdminAccess>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("activation", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>($"{DatabaseOptions.SectionName}:AutoCreate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<EldoradoDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow }));

// A freshly pasted, locally verified key is exchanged for a revocable device token.
app.MapPost("/api/v1/activations", async (
    ActivateLicenseRequest request,
    LicenseKeyVerifier verifier,
    DeviceTokenService tokens,
    EntitlementService entitlements,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!verifier.TryVerify(request.LicenseKey, request.MachineId, out var verified, out var error))
    {
        return Results.BadRequest(new { message = error });
    }

    var now = DateTimeOffset.UtcNow;
    var license = await db.Licenses
        .Include(x => x.Activations)
        .Include(x => x.Subscriptions)
        .SingleOrDefaultAsync(x => x.KeyId == verified!.Info.KeyId, cancellationToken);

    if (license is not null && !Hashing.FixedTimeEquals(license.KeyDigest, verified.KeyDigest))
    {
        return Results.Conflict(new { message = "Collisione dell'identificatore licenza: contatta l'assistenza." });
    }

    if (license is null)
    {
        license = new LicenseEntitlement
        {
            KeyId = verified.Info.KeyId,
            KeyDigest = verified.KeyDigest,
            IssuedAtUtc = new DateTimeOffset(verified.Info.Issued.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
            ExpiresAtUtc = verified.Info.ExpiresAtUtc,
            IsDeviceLocked = verified.Info.IsDeviceLocked,
            DeviceTagDigest = verified.DeviceTagDigest,
            State = EntitlementState.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        license.Subscriptions.Add(new Subscription
        {
            State = SubscriptionState.Active,
            StartsAtUtc = now,
            EndsAtUtc = verified.Info.ExpiresAtUtc,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.Licenses.Add(license);
    }

    if (license.State is EntitlementState.Revoked or EntitlementState.Suspended)
    {
        return Results.Conflict(new { message = "Questa licenza è stata disattivata dal server." });
    }

    var existing = license.Activations.SingleOrDefault(x => x.MachineDigest == verified.MachineDigest);
    if (existing is null && !license.IsDeviceLocked)
    {
        var maximum = Math.Max(1, app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LicensingOptions>>()
            .Value.MaxDevicesForFloatingLicense);
        var activeDevices = license.Activations.Count(x => x.RevokedAtUtc is null && x.TokenExpiresAtUtc > now);
        if (activeDevices >= maximum)
        {
            return Results.Conflict(new { message = $"Questa licenza può essere attivata su massimo {maximum} PC." });
        }
    }

    var token = tokens.CreateToken();
    var tokenDays = Math.Clamp(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LicensingOptions>>()
        .Value.DeviceTokenDays, 1, 365);

    if (existing is null)
    {
        existing = new DeviceActivation
        {
            License = license,
            MachineDigest = verified.MachineDigest,
            DeviceName = Limit(request.DeviceName, 200, "PC Windows"),
            CreatedAtUtc = now
        };
        license.Activations.Add(existing);
    }

    existing.RevokedAtUtc = null;
    existing.DeviceName = Limit(request.DeviceName, 200, existing.DeviceName);
    existing.TokenDigest = tokens.Digest(token);
    existing.TokenPrefix = token[..Math.Min(token.Length, 18)];
    existing.TokenExpiresAtUtc = now.AddDays(tokenDays);
    existing.LastSeenAtUtc = now;
    license.UpdatedAtUtc = now;

    await db.SaveChangesAsync(cancellationToken);
    var entitlement = await entitlements.DescribeAsync(license, cancellationToken);
    if (entitlement.State != EntitlementState.Active)
    {
        return Results.Conflict(new { message = entitlement.Message });
    }

    return Results.Ok(new ActivateLicenseResponse(token, entitlement));
}).RequireRateLimiting("activation");

app.MapGet("/api/v1/entitlements/current", async (
    HttpRequest request,
    DeviceAuthentication authentication,
    EntitlementService entitlements,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    var (session, failure) = await EndpointGuards.RequireDeviceAsync(request, authentication, db, cancellationToken);
    return failure ?? Results.Ok(await entitlements.DescribeAsync(session!.License, cancellationToken));
});

app.MapGet("/api/v1/policy", async (
    HttpRequest request,
    DeviceAuthentication authentication,
    EntitlementService entitlements,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    var (session, failure) = await EndpointGuards.RequireDeviceAsync(request, authentication, db, cancellationToken);
    return failure ?? Results.Ok(await entitlements.DescribePolicyAsync(cancellationToken));
});

app.MapGet("/api/v1/configuration", async (
    HttpRequest request,
    DeviceAuthentication authentication,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    var (session, failure) = await EndpointGuards.RequireDeviceAsync(request, authentication, db, cancellationToken);
    if (failure is not null)
    {
        return failure;
    }

    var configuration = session!.Activation.Configuration;
    return configuration is null
        ? Results.NotFound()
        : Results.Ok(new BotConfigurationResponse(configuration.ConfigurationJson, configuration.UpdatedAtUtc));
});

app.MapPut("/api/v1/configuration", async (
    BotConfigurationRequest request,
    HttpRequest http,
    DeviceAuthentication authentication,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    if (request.ConfigurationJson.Length > 512_000)
    {
        return Results.BadRequest(new { message = "Configurazione troppo grande." });
    }

    try
    {
        using var _ = JsonDocument.Parse(request.ConfigurationJson);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { message = "La configurazione deve essere JSON valido." });
    }

    var (session, failure) = await EndpointGuards.RequireDeviceAsync(http, authentication, db, cancellationToken);
    if (failure is not null)
    {
        return failure;
    }

    var configuration = session!.Activation.Configuration;
    if (configuration is null)
    {
        configuration = new BotConfiguration { DeviceActivationId = session.Activation.Id };
        db.BotConfigurations.Add(configuration);
    }

    configuration.ConfigurationJson = request.ConfigurationJson;
    configuration.UpdatedAtUtc = request.UpdatedAtUtc;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new BotConfigurationResponse(configuration.ConfigurationJson, configuration.UpdatedAtUtc));
});

app.MapPost("/api/v1/automation-events", async (
    AutomationAuditEventRequest request,
    HttpRequest http,
    DeviceAuthentication authentication,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    var (session, failure) = await EndpointGuards.RequireDeviceAsync(http, authentication, db, cancellationToken);
    if (failure is not null)
    {
        return failure;
    }

    db.AutomationAuditEvents.Add(new AutomationAuditEvent
    {
        DeviceActivationId = session!.Activation.Id,
        Kind = Limit(request.Kind, 120, "unknown"),
        RequestId = Limit(request.RequestId, 200, null),
        BuyerId = Limit(request.BuyerId, 200, null),
        Detail = Limit(request.Detail, 4000, null),
        OccurredAtUtc = request.OccurredAtUtc
    });
    await db.SaveChangesAsync(cancellationToken);
    return Results.Accepted();
});

// Administrative endpoints are deliberately separate from device authentication.
app.MapGet("/api/v1/admin/licenses", async (
    HttpRequest request,
    AdminAccess admin,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!admin.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }

    var licences = await db.Licenses
        .Include(x => x.Customer)
        .Include(x => x.Subscriptions)
        .OrderByDescending(x => x.CreatedAtUtc)
        .Select(x => new
        {
            x.KeyId,
            x.State,
            x.ExpiresAtUtc,
            x.AutomationAllowed,
            x.MessagingAllowed,
            x.MaxMessagesPerHour,
            Customer = x.Customer == null ? null : x.Customer.Email ?? x.Customer.DisplayName,
            Subscriptions = x.Subscriptions.Select(s => new { s.State, s.StartsAtUtc, s.EndsAtUtc })
        })
        .ToListAsync(cancellationToken);
    return Results.Ok(licences);
});

app.MapPatch("/api/v1/admin/licenses/{keyId}", async (
    string keyId,
    UpdateLicensePolicyRequest request,
    HttpRequest http,
    AdminAccess admin,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!admin.IsAuthorized(http))
    {
        return Results.Unauthorized();
    }

    var license = await db.Licenses.SingleOrDefaultAsync(x => x.KeyId == keyId.Trim().ToUpperInvariant(), cancellationToken);
    if (license is null)
    {
        return Results.NotFound();
    }

    if (request.AutomationAllowed is { } automation) license.AutomationAllowed = automation;
    if (request.MessagingAllowed is { } messaging) license.MessagingAllowed = messaging;
    if (request.MaxMessagesPerHour is { } rate) license.MaxMessagesPerHour = Math.Clamp(rate, 1, 10_000);
    if (request.State is { } state) license.State = state;
    if (request.Note is not null) license.Note = Limit(request.Note, 2000, null);
    license.UpdatedAtUtc = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/v1/admin/orders", async (
    HttpRequest request,
    AdminAccess admin,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!admin.IsAuthorized(request))
    {
        return Results.Unauthorized();
    }

    var orders = await db.Orders
        .OrderByDescending(x => x.CreatedAtUtc)
        .Take(500)
        .Select(x => new { x.ExternalOrderId, x.Amount, x.Currency, x.Status, x.CreatedAtUtc, LicenseKeyId = x.License!.KeyId })
        .ToListAsync(cancellationToken);
    return Results.Ok(orders);
});

app.MapPost("/api/v1/admin/orders", async (
    CreateOrderRequest request,
    HttpRequest http,
    AdminAccess admin,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!admin.IsAuthorized(http))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.ExternalOrderId) || request.Amount < 0 || string.IsNullOrWhiteSpace(request.Currency))
    {
        return Results.BadRequest(new { message = "ID ordine, importo e valuta sono obbligatori." });
    }

    if (await db.Orders.AnyAsync(x => x.ExternalOrderId == request.ExternalOrderId.Trim(), cancellationToken))
    {
        return Results.Conflict(new { message = "Esiste già un ordine con questo ID esterno." });
    }

    LicenseEntitlement? license = null;
    if (!string.IsNullOrWhiteSpace(request.LicenseKeyId))
    {
        license = await db.Licenses.SingleOrDefaultAsync(x => x.KeyId == request.LicenseKeyId.Trim().ToUpperInvariant(), cancellationToken);
        if (license is null)
        {
            return Results.BadRequest(new { message = "Licenza non trovata." });
        }
    }

    CustomerAccount? customer = null;
    if (!string.IsNullOrWhiteSpace(request.CustomerEmail))
    {
        var email = request.CustomerEmail.Trim().ToLowerInvariant();
        customer = await db.Customers.SingleOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (customer is null)
        {
            customer = new CustomerAccount { Email = email, DisplayName = email };
            db.Customers.Add(customer);
        }
    }

    db.Orders.Add(new OrderRecord
    {
        ExternalOrderId = request.ExternalOrderId.Trim(),
        Customer = customer,
        License = license,
        Amount = request.Amount,
        Currency = Limit(request.Currency.Trim().ToUpperInvariant(), 8, "EUR"),
        Status = Limit(request.Status, 64, "pending"),
        Note = Limit(request.Note, 4000, null)
    });
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created("/api/v1/admin/orders", new { request.ExternalOrderId });
});

app.MapPut("/api/v1/admin/policy", async (
    RemotePolicyResponse request,
    HttpRequest http,
    AdminAccess admin,
    EntitlementService entitlements,
    EldoradoDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!admin.IsAuthorized(http))
    {
        return Results.Unauthorized();
    }

    var policy = await entitlements.GetPolicyAsync(cancellationToken);
    policy.AutomationAllowed = request.AutomationAllowed;
    policy.MessagingAllowed = request.MessagingAllowed;
    policy.MaxMessagesPerHour = request.MaxMessagesPerHour is { } limit ? Math.Clamp(limit, 1, 10_000) : null;
    policy.MinimumClientVersion = Limit(request.MinimumClientVersion, 64, null);
    policy.Message = Limit(request.Message, 2000, "Servizio aggiornato");
    policy.ChangedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

app.Run();

static string? Limit(string? value, int maximum, string? fallback)
{
    var text = value?.Trim();
    return string.IsNullOrWhiteSpace(text)
        ? fallback
        : text.Length <= maximum ? text : text[..maximum];
}
