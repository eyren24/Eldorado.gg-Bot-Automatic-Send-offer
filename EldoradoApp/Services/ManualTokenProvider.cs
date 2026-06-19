using System.Text.Json;

namespace EldoradoApp.Services;

/// <summary>
/// Uses an IdToken (JWT) pasted by hand — e.g. copied from a logged-in browser's
/// <c>__Host-EldoradoIdToken</c> cookie. No refresh: when it expires the user pastes a
/// fresh one. The token's own <c>exp</c> claim drives the expiry display.
/// </summary>
public sealed class ManualTokenProvider : IEldoradoTokenProvider
{
    private string? _idToken;
    private DateTimeOffset _expiresAt;

    /// <summary>True while a token is set (it may still be expired — see <see cref="IsExpired"/>).</summary>
    public bool IsSignedIn => _idToken is not null;

    public DateTimeOffset? ExpiresAt => _idToken is null ? null : _expiresAt;

    public bool IsExpired => _idToken is not null && DateTimeOffset.UtcNow >= _expiresAt;

    public string? Email { get; private set; }

    /// <summary>
    /// Accepts a pasted token, tolerating a leading <c>__Host-EldoradoIdToken=</c>, quotes or
    /// whitespace. Throws <see cref="FormatException"/> if it isn't a JWT.
    /// </summary>
    public void SetToken(string raw)
    {
        var token = Normalize(raw);
        if (token.Split('.').Length != 3)
        {
            throw new FormatException("Non sembra un JWT (servono tre parti separate da punti). Incolla il valore eyJ… del cookie __Host-EldoradoIdToken.");
        }

        var claims = DecodePayload(token);
        _idToken = token;
        _expiresAt = TryGetExp(claims) ?? DateTimeOffset.UtcNow.AddMinutes(30);
        Email = TryGetString(claims, "email");
    }

    public void Clear()
    {
        _idToken = null;
        _expiresAt = default;
        Email = null;
    }

    public Task<string?> GetIdTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        => Task.FromResult(_idToken);

    private static string Normalize(string raw)
    {
        var token = raw.Trim().Trim('"').Trim();
        const string cookiePrefix = EldoradoApiOptions.IdTokenCookieName + "=";
        if (token.StartsWith(cookiePrefix, StringComparison.OrdinalIgnoreCase))
        {
            token = token[cookiePrefix.Length..].Trim();
        }

        // Strip a trailing ';' if the whole cookie line was pasted.
        var semicolon = token.IndexOf(';');
        if (semicolon >= 0)
        {
            token = token[..semicolon].Trim();
        }

        return token;
    }

    private static Dictionary<string, JsonElement>? DecodePayload(string token)
    {
        try
        {
            var payload = token.Split('.')[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = Convert.FromBase64String(payload);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetExp(Dictionary<string, JsonElement>? claims)
    {
        if (claims is not null && claims.TryGetValue("exp", out var exp) && exp.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return null;
    }

    private static string? TryGetString(Dictionary<string, JsonElement>? claims, string key)
        => claims is not null && claims.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
