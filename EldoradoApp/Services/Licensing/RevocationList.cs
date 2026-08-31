using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EldoradoApp.Services.Licensing;

/// <summary>The published file: the cancelled key ids plus a signature over them.</summary>
public sealed class RevocationDocument
{
    [JsonPropertyName("revoked")] public List<string> Revoked { get; set; } = [];

    /// <summary>Base64 ECDSA signature over <see cref="LicenseCodec.RevocationDigest"/>.</summary>
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";

    [JsonPropertyName("updated")] public DateTimeOffset Updated { get; set; }
}

/// <summary>
/// The one optional online piece, and the answer to "what if I need to kill a key I have
/// already delivered?". A small signed JSON file - a public gist is enough - lists the
/// cancelled key ids; the app fetches it now and then and caches it.
/// </summary>
/// <remarks>
/// Deliberately fail-open: no URL configured, no network, a firewall or a dead gist all
/// leave paying customers working. It can only ever take a key away, never grant one, so
/// an attacker who blocks the fetch gains nothing they did not already have. The signature
/// is what stops someone else's file from revoking your customers.
/// </remarks>
public static class RevocationList
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp", "revoked.json");

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static HashSet<string>? _ids;
    private static DateTimeOffset _fetchedUtc;

    /// <summary>True once this run has a list to consult (from the cache or from the network).</summary>
    public static bool IsLoaded => _ids is not null;

    /// <summary>Whether <paramref name="keyId"/> has been cancelled. Unknown means "not cancelled".</summary>
    public static bool IsRevoked(string keyId) =>
        _ids is not null && _ids.Contains(keyId.Trim().ToUpperInvariant());

    /// <summary>Reads the last list saved to disk, so the very first check needs no network.</summary>
    public static void LoadCache()
    {
        if (!LicenseOptions.IsConfigured || LicenseOptions.RevocationListUrl.Length == 0)
        {
            return;
        }

        try
        {
            if (File.Exists(CachePath) && Accept(File.ReadAllText(CachePath)))
            {
                _fetchedUtc = File.GetLastWriteTimeUtc(CachePath);
            }
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Revocation cache unreadable: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-fetches the list when it is stale. Never throws and never blocks the caller for
    /// long: a slow or missing server must not keep a paying customer out of the app.
    /// </summary>
    public static async Task RefreshAsync(bool force = false)
    {
        var url = LicenseOptions.RevocationListUrl;
        if (!LicenseOptions.IsConfigured || url.Length == 0)
        {
            return;
        }

        if (!force && _ids is not null &&
            DateTimeOffset.UtcNow - _fetchedUtc < LicenseOptions.RevocationRefreshInterval)
        {
            return;
        }

        if (!await Gate.WaitAsync(TimeSpan.Zero))
        {
            return;   // another refresh is already in flight
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync(url);

            if (!Accept(json))
            {
                ApiLog.Write("Revocation list rejected: signature does not match the app's public key.");
                return;
            }

            _fetchedUtc = DateTimeOffset.UtcNow;

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, json);
        }
        catch (Exception ex)
        {
            // Offline, blocked, 404: keep whatever the cache gave us and carry on.
            ApiLog.Write($"Revocation list not refreshed: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Verifies the signature before believing a single id in the document.</summary>
    private static bool Accept(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<RevocationDocument>(json);
            if (doc is null)
            {
                return false;
            }

            var signature = Convert.FromBase64String(doc.Signature);
            if (!LicenseCodec.Verify(LicenseCodec.RevocationDigest(doc.Revoked), signature, LicenseOptions.PublicKey))
            {
                return false;
            }

            _ids = new HashSet<string>(
                doc.Revoked.Select(id => id.Trim().ToUpperInvariant()), StringComparer.Ordinal);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
