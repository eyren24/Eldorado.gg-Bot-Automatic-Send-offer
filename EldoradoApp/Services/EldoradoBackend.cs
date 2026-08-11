using System.Net.Http;
using System.Net.Http.Headers;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Composition root for the live Eldorado Seller API: owns the auth services and a
/// single authenticated <see cref="HttpClient"/>, and exposes the real data clients.
/// Sign-in works two ways — Cognito SRP (email/password, persisted via
/// <see cref="CredentialStore"/>) and Google OAuth (Hosted UI, refresh token persisted
/// via <see cref="OAuthTokenStore"/>) — and the active one is tracked by
/// <see cref="SellerSession"/>.
/// </summary>
public sealed class EldoradoBackend : IDisposable
{
    private readonly HttpClient _http;
    private readonly SellerSession _session = new();

    /// <summary>SRP email/password authentication.</summary>
    public EldoradoAuthService Auth { get; }

    /// <summary>Google (Hosted UI OAuth + PKCE) authentication.</summary>
    public EldoradoOAuthService OAuth { get; }

    /// <summary>Hand-pasted IdToken (copied from a logged-in browser session).</summary>
    public ManualTokenProvider ManualToken { get; } = new();

    public IEldoradoClient Orders { get; }
    public IBoostingRequestClient BoostingRequests { get; }
    public IBoostingOfferClient BoostingOffers { get; }

    public EldoradoBackend()
    {
        Auth = new EldoradoAuthService();
        OAuth = new EldoradoOAuthService();

        ApiLog.StartSession("Eldorado backend started");

        // Pipeline: auth (adds cookie + 401 retry) → logging (records every call) → network.
        var logging = new ApiLoggingHandler
        {
            InnerHandler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }
        };
        var handler = new EldoradoAuthHandler(_session) { InnerHandler = logging };

        _http = new HttpClient(handler) { BaseAddress = new Uri(EldoradoApiOptions.BaseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Orders = new EldoradoClient(_http);
        BoostingRequests = new EldoradoBoostingRequestClient(_http);
        BoostingOffers = new EldoradoBoostingOfferClient(_http);
    }

    /// <summary>How the current session was established (None when signed out).</summary>
    public SellerAuthMethod AuthMethod => _session.Method;

    /// <summary>
    /// Builds an auto-offer engine bound to the live clients and a settings source.
    /// Passing <paramref name="messages"/> makes it fire the seller's follow-up message
    /// (with banner) right after each offer is submitted.
    /// </summary>
    public AutoOfferEngine CreateAutoOfferEngine(
        Func<BoostingBotSettings> settingsProvider, OfferMessageDispatcher? messages = null)
        => new(BoostingRequests, BoostingOffers, settingsProvider, messages);

    public bool IsSignedIn => _session.IsSignedIn;

    /// <summary>The IdToken currently in use, when the session came from a pasted/borrowed token.</summary>
    public string? CurrentIdToken => ManualToken.Token;

    /// <summary>
    /// Restores a session from persisted secrets — a stored Google refresh token first,
    /// otherwise stored email/password credentials. Returns false if neither works.
    /// </summary>
    public async Task<bool> TrySignInFromStoreAsync(CancellationToken cancellationToken = default)
    {
        // A persisted hand-pasted token, if still valid, wins (it's the explicit current method).
        if (ManualTokenStore.Load() is { } savedToken)
        {
            try
            {
                ManualToken.SetToken(savedToken);
                if (!ManualToken.IsExpired)
                {
                    _session.Use(ManualToken, SellerAuthMethod.ManualToken);
                    return true;
                }
            }
            catch (FormatException)
            {
                // Ignore a corrupt stored token.
            }

            ManualToken.Clear();
            ManualTokenStore.Clear();
        }

        if (OAuthTokenStore.Exists && await OAuth.TryRestoreAsync(cancellationToken).ConfigureAwait(false))
        {
            _session.Use(OAuth, SellerAuthMethod.Google);
            return true;
        }

        var credentials = CredentialStore.Load();
        if (credentials is not null)
        {
            try
            {
                await Auth.SignInAsync(credentials.Email, credentials.Password, cancellationToken).ConfigureAwait(false);
                _session.Use(Auth, SellerAuthMethod.EmailPassword);
                return true;
            }
            catch
            {
                // Fall through: stored credentials no longer valid.
            }
        }

        return false;
    }

    /// <summary>Signs in with email/password (SRP) and persists the credentials on success.</summary>
    public async Task SignInAndRememberAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        await Auth.SignInAsync(email, password, cancellationToken).ConfigureAwait(false);
        CredentialStore.Save(new EldoradoCredentials(email, password));
        _session.Use(Auth, SellerAuthMethod.EmailPassword);
    }

    /// <summary>
    /// Signs in with a hand-pasted IdToken (from a logged-in browser session) and persists it.
    /// Throws <see cref="FormatException"/> if the value isn't a JWT.
    /// </summary>
    public void UseManualToken(string token)
    {
        ManualToken.SetToken(token);
        ManualTokenStore.Save(token);
        _session.Use(ManualToken, SellerAuthMethod.ManualToken);
        ApiLog.Write($"Manual token set: email={ManualToken.Email ?? "?"}, expires={ManualToken.ExpiresAt?.ToLocalTime():HH:mm:ss}, expiredNow={ManualToken.IsExpired}");
    }

    public void SignOut()
    {
        Auth.SignOut();
        OAuth.SignOut();
        ManualToken.Clear();
        CredentialStore.Clear();
        OAuthTokenStore.Clear();
        ManualTokenStore.Clear();
        _session.Clear();
    }

    public void Dispose()
    {
        _http.Dispose();
        Auth.Dispose();
        OAuth.Dispose();
    }
}
