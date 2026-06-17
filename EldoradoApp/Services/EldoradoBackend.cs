using System.Net.Http;
using System.Net.Http.Headers;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// Composition root for the live Eldorado Seller API: owns the auth service and a
/// single authenticated <see cref="HttpClient"/>, and exposes the real data clients.
/// Sign-in uses Cognito SRP (email/password), persisted via <see cref="CredentialStore"/>.
/// </summary>
public sealed class EldoradoBackend : IDisposable
{
    private readonly HttpClient _http;

    public EldoradoAuthService Auth { get; }
    public IEldoradoClient Orders { get; }
    public IBoostingRequestClient BoostingRequests { get; }
    public IBoostingOfferClient BoostingOffers { get; }

    public EldoradoBackend()
    {
        Auth = new EldoradoAuthService();

        var handler = new EldoradoAuthHandler(Auth)
        {
            InnerHandler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }
        };

        _http = new HttpClient(handler) { BaseAddress = new Uri(EldoradoApiOptions.BaseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Orders = new EldoradoClient(_http);
        BoostingRequests = new EldoradoBoostingRequestClient(_http);
        BoostingOffers = new EldoradoBoostingOfferClient(_http);
    }

    /// <summary>Builds an auto-offer engine bound to the live clients and a settings source.</summary>
    public AutoOfferEngine CreateAutoOfferEngine(Func<BoostingBotSettings> settingsProvider)
        => new(BoostingRequests, BoostingOffers, settingsProvider);

    public bool IsSignedIn => Auth.IsSignedIn;

    /// <summary>Signs in using stored credentials when present. Returns false if absent or failed.</summary>
    public async Task<bool> TrySignInFromStoreAsync(CancellationToken cancellationToken = default)
    {
        var credentials = CredentialStore.Load();
        if (credentials is null)
        {
            return false;
        }

        try
        {
            await Auth.SignInAsync(credentials.Email, credentials.Password, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Signs in with the given credentials and persists them on success.</summary>
    public async Task SignInAndRememberAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        await Auth.SignInAsync(email, password, cancellationToken).ConfigureAwait(false);
        CredentialStore.Save(new EldoradoCredentials(email, password));
    }

    public void SignOut()
    {
        Auth.SignOut();
        CredentialStore.Clear();
    }

    public void Dispose()
    {
        _http.Dispose();
        Auth.Dispose();
    }
}
