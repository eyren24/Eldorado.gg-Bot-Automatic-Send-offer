using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;

namespace EldoradoApp.Services;

/// <summary>
/// "Sign in with Google" for the Eldorado seller account, via the Cognito Hosted UI
/// using OAuth Authorization-Code + PKCE. The interactive browser step (consent /
/// Google login) is driven by a WebView2 window in the UI layer; this service builds
/// the authorize URL, exchanges the returned code for tokens at <c>/oauth2/token</c>,
/// and refreshes the IdToken as needed.
/// </summary>
/// <remarks>
/// As of now the bot's Hosted UI is not enabled by Eldorado (the authorize page returns
/// "Login pages unavailable"), so this flow is wired and ready but won't complete until
/// Eldorado turns on Google support for client <see cref="EldoradoApiOptions.ClientId"/>.
/// </remarks>
public sealed class EldoradoOAuthService : IEldoradoTokenProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = CreateHttpClient();

    private string? _idToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public bool IsSignedIn => _refreshToken is not null || _idToken is not null;

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        // Present as a real browser; the Cognito domain sits behind Cloudflare and may
        // challenge requests that look like bots/raw clients.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    /// <summary>
    /// Builds the Hosted UI authorize URL and the matching PKCE verifier. Open the URL in
    /// a browser/WebView2; on success it redirects to <see cref="EldoradoApiOptions.OAuthRedirectUri"/>
    /// with a <c>?code=…</c>, which must be passed back to <see cref="CompleteSignInAsync"/>
    /// together with the returned verifier.
    /// </summary>
    public (string authorizeUrl, string codeVerifier) BeginSignIn()
    {
        var verifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = EldoradoApiOptions.ClientId;
        query["response_type"] = "code";
        query["scope"] = EldoradoApiOptions.OAuthScopes;
        query["redirect_uri"] = EldoradoApiOptions.OAuthRedirectUri;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";

        // Go straight to Google, bypassing Cognito's (currently unavailable) managed login page.
        if (!string.IsNullOrEmpty(EldoradoApiOptions.OAuthIdentityProvider))
        {
            query["identity_provider"] = EldoradoApiOptions.OAuthIdentityProvider;
        }

        return ($"{EldoradoApiOptions.AuthorizeEndpoint}?{query}", verifier);
    }

    /// <summary>Exchanges the authorization code for tokens and persists the refresh token.</summary>
    public async Task CompleteSignInAsync(string code, string codeVerifier, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = EldoradoApiOptions.ClientId,
                ["code"] = code,
                ["redirect_uri"] = EldoradoApiOptions.OAuthRedirectUri,
                ["code_verifier"] = codeVerifier,
            };

            var tokens = await PostTokenAsync(form, cancellationToken).ConfigureAwait(false);
            ApplyTokens(tokens, isRefresh: false);

            if (_refreshToken is not null)
            {
                OAuthTokenStore.Save(new OAuthTokens(_refreshToken));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restores a previous session from the stored refresh token. Returns false if none/expired.</summary>
    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        var stored = OAuthTokenStore.Load();
        if (stored?.RefreshToken is null)
        {
            return false;
        }

        _refreshToken = stored.RefreshToken;
        var token = await GetIdTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        return token is not null;
    }

    public async Task<string?> GetIdTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _idToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _idToken;
            }

            if (_refreshToken is null)
            {
                return _idToken;
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = EldoradoApiOptions.ClientId,
                ["refresh_token"] = _refreshToken,
            };

            var tokens = await PostTokenAsync(form, cancellationToken).ConfigureAwait(false);
            ApplyTokens(tokens, isRefresh: true);
            return _idToken;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SignOut()
    {
        _idToken = null;
        _refreshToken = null;
        _expiresAt = default;
    }

    private async Task<TokenResponse> PostTokenAsync(
        Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, EldoradoApiOptions.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Scambio token fallito ({(int)response.StatusCode}): {body}");
        }

        return System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body)
               ?? throw new InvalidOperationException("Risposta token vuota dal server.");
    }

    private void ApplyTokens(TokenResponse tokens, bool isRefresh)
    {
        if (!string.IsNullOrEmpty(tokens.IdToken))
        {
            _idToken = tokens.IdToken;
        }

        // The refresh grant does not return a new refresh token; keep the existing one.
        if (!isRefresh && !string.IsNullOrEmpty(tokens.RefreshToken))
        {
            _refreshToken = tokens.RefreshToken;
        }

        var lifetime = tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600;
        // Refresh a minute early to avoid using a token that expires mid-request.
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime - 60);
    }

    private static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("id_token")] public string? IdToken { get; init; }
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    }
}
