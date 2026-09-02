using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EldoradoApp.Contracts;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

public sealed record RemoteOperationResult(bool Success, string Message, EntitlementResponse? Entitlement = null);

/// <summary>Desktop client for the server-side licence and policy control plane.</summary>
public sealed class RemoteEntitlementService(
    Func<RemoteControlSettings> settingsProvider,
    Action persistSilently) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? Changed;

    public bool IsConfigured => settingsProvider().IsConfigured;
    public bool IsRequired => settingsProvider().RequireServer;
    public bool HasDeviceToken => RemoteDeviceTokenStore.Load(settingsProvider().BaseUrl) is not null;

    /// <summary>The configured endpoint, for the settings screen. Never contains a secret.</summary>
    public string BaseUrl => settingsProvider().BaseUrl;

    public bool SyncsConfiguration => settingsProvider().SyncConfiguration;

    public string StatusText
    {
        get
        {
            var settings = settingsProvider();
            if (!settings.IsConfigured) return "Server non configurato: licenza offline locale attiva.";
            if (settings.LastEntitlement is { } verdict)
            {
                var access = verdict.AutomationAllowed ? "autorizzato" : "bloccato";
                return $"Server {access} · controllo {verdict.CheckedAtUtc.ToLocalTime():dd/MM HH:mm}";
            }

            return string.IsNullOrWhiteSpace(settings.LastError)
                ? "Server configurato, in attesa della prima verifica."
                : $"Server non raggiungibile: {settings.LastError}";
        }
    }

    public bool AllowsAutomation() => Allows(static snapshot => snapshot.AutomationAllowed);
    public bool AllowsMessaging() => Allows(static snapshot => snapshot.MessagingAllowed);

    /// <summary>Activates or renews the revocable server session after local key verification succeeds.</summary>
    public async Task<RemoteOperationResult> ActivateAsync(string licenseKey, string machineId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new RemoteOperationResult(true, "Server non configurato: resta attiva la licenza offline.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = new ActivateLicenseRequest(
                licenseKey,
                machineId,
                Environment.MachineName,
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString());

            using var response = await _http.PostAsJsonAsync(Endpoint("/api/v1/activations"), request, Json, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                RecordFailure(message);
                return new RemoteOperationResult(false, message);
            }

            var result = await response.Content.ReadFromJsonAsync<ActivateLicenseResponse>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (result is null || string.IsNullOrWhiteSpace(result.DeviceToken))
            {
                const string message = "Il server ha risposto senza un token del dispositivo.";
                RecordFailure(message);
                return new RemoteOperationResult(false, message);
            }

            RemoteDeviceTokenStore.Save(new RemoteDeviceToken(
                settingsProvider().BaseUrl, result.DeviceToken, result.Entitlement.KeyId, DateTimeOffset.UtcNow));
            Apply(result.Entitlement);
            return new RemoteOperationResult(true, result.Entitlement.Message, result.Entitlement);
        }
        catch (OperationCanceledException)
        {
            return new RemoteOperationResult(false, "Verifica server annullata.");
        }
        catch (Exception ex)
        {
            var message = $"Server non raggiungibile: {ex.Message}";
            RecordFailure(message);
            return new RemoteOperationResult(false, message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fetches the current control-plane verdict using only the stored device token.</summary>
    public async Task<RemoteOperationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new RemoteOperationResult(true, "Server non configurato.");
        }

        var token = RemoteDeviceTokenStore.Load(settingsProvider().BaseUrl);
        if (token is null)
        {
            const string message = "Nessun token server per questo PC: riapplica la licenza dopo aver configurato l'endpoint.";
            RecordFailure(message);
            return new RemoteOperationResult(false, message);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("/api/v1/entitlements/current"));
            request.Headers.TryAddWithoutValidation("X-Eldorado-Device-Token", token.Token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                RemoteDeviceTokenStore.Clear();
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                RecordFailure(message);
                return new RemoteOperationResult(false, message);
            }

            var entitlement = await response.Content.ReadFromJsonAsync<EntitlementResponse>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (entitlement is null)
            {
                const string message = "Risposta licenza del server non leggibile.";
                RecordFailure(message);
                return new RemoteOperationResult(false, message);
            }

            Apply(entitlement);
            return new RemoteOperationResult(true, entitlement.Message, entitlement);
        }
        catch (OperationCanceledException)
        {
            return new RemoteOperationResult(false, "Verifica server annullata.");
        }
        catch (Exception ex)
        {
            var message = $"Server non raggiungibile: {ex.Message}";
            RecordFailure(message);
            return new RemoteOperationResult(false, message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Backs up a configuration only after an explicit Save; Eldorado credentials are not part of this model.</summary>
    public async Task PushConfigurationAsync(BoostingBotSettings settings, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || !settingsProvider().SyncConfiguration)
        {
            return;
        }

        var token = RemoteDeviceTokenStore.Load(settingsProvider().BaseUrl);
        if (token is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(settings, Json);
            var body = new BotConfigurationRequest(json, DateTimeOffset.UtcNow);
            using var request = new HttpRequestMessage(HttpMethod.Put, Endpoint("/api/v1/configuration"))
            {
                Content = JsonContent.Create(body, options: Json)
            };
            request.Headers.TryAddWithoutValidation("X-Eldorado-Device-Token", token.Token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ApiLog.Write($"Remote configuration sync failed: {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Remote configuration sync failed: {ex.Message}");
        }
    }

    public async Task AuditAsync(AutomationAuditEventRequest audit, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return;
        }

        var token = RemoteDeviceTokenStore.Load(settingsProvider().BaseUrl);
        if (token is null)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("/api/v1/automation-events"))
            {
                Content = JsonContent.Create(audit, options: Json)
            };
            request.Headers.TryAddWithoutValidation("X-Eldorado-Device-Token", token.Token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ApiLog.Write($"Remote audit failed: {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Remote audit failed: {ex.Message}");
        }
    }

    public void Configure(string? baseUrl, bool requireServer, bool syncConfiguration)
    {
        var settings = settingsProvider();
        var oldUrl = settings.BaseUrl;
        settings.BaseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        settings.RequireServer = requireServer;
        settings.SyncConfiguration = syncConfiguration;
        settings.Normalize();

        if (!string.Equals(oldUrl, settings.BaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            RemoteDeviceTokenStore.Clear();
            settings.LastEntitlement = null;
        }

        persistSilently();
        Changed?.Invoke();
    }

    public void ForgetDeviceSession()
    {
        RemoteDeviceTokenStore.Clear();
        var settings = settingsProvider();
        settings.LastEntitlement = null;
        settings.LastError = "";
        persistSilently();
        Changed?.Invoke();
    }

    private bool Allows(Func<RemoteEntitlementSnapshot, bool> selector)
    {
        var settings = settingsProvider();
        if (!settings.RequireServer)
        {
            return true;
        }

        var snapshot = settings.LastEntitlement;
        return snapshot is { State: EntitlementState.Active } &&
               snapshot.OfflineUntilUtc is { } deadline && DateTimeOffset.UtcNow <= deadline &&
               selector(snapshot) &&
               IsVersionSupported(snapshot.MinimumClientVersion);
    }

    private void Apply(EntitlementResponse entitlement)
    {
        var settings = settingsProvider();
        settings.LastEntitlement = RemoteEntitlementSnapshot.From(entitlement);
        settings.LastError = "";
        persistSilently();
        Changed?.Invoke();
    }

    private void RecordFailure(string message)
    {
        var settings = settingsProvider();
        settings.LastError = message;
        persistSilently();
        Changed?.Invoke();
    }

    private Uri Endpoint(string path)
    {
        var root = settingsProvider().BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(root, UriKind.Absolute), path.TrimStart('/'));
    }

    private static bool IsVersionSupported(string? minimum)
    {
        if (string.IsNullOrWhiteSpace(minimum) || !Version.TryParse(minimum, out var required))
        {
            return true;
        }

        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
        return current >= required;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var document = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
            if (document.ValueKind == JsonValueKind.Object && document.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? $"Server: {(int)response.StatusCode}";
            }
        }
        catch
        {
            // Fall through to the concise status message below.
        }

        return $"Server: {(int)response.StatusCode} {response.ReasonPhrase}";
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
