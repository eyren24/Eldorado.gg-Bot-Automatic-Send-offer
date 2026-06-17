using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.Extensions.CognitoAuthentication;
using Amazon.Runtime;

namespace EldoradoApp.Services;

/// <summary>
/// Authenticates a seller against the Eldorado Cognito user pool using the SRP
/// (Secure Remote Password) email/password flow and hands out a valid IdToken,
/// refreshing it automatically when it expires.
/// </summary>
public sealed class EldoradoAuthService : IEldoradoTokenProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AmazonCognitoIdentityProviderClient _provider;
    private readonly CognitoUserPool _pool;
    private CognitoUser? _user;

    public EldoradoAuthService()
    {
        _provider = new AmazonCognitoIdentityProviderClient(
            new AnonymousAWSCredentials(),
            RegionEndpoint.GetBySystemName(EldoradoApiOptions.Region));

        _pool = new CognitoUserPool(EldoradoApiOptions.UserPoolId, EldoradoApiOptions.ClientId, _provider);
    }

    public bool IsSignedIn => _user?.SessionTokens is not null;

    /// <summary>Performs an SRP sign-in. Throws on invalid credentials or an unhandled challenge.</summary>
    public async Task SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var user = new CognitoUser(email, EldoradoApiOptions.ClientId, _pool, _provider);
            var response = await user.StartWithSrpAuthAsync(new InitiateSrpAuthRequest { Password = password })
                .ConfigureAwait(false);

            if (response.AuthenticationResult is null)
            {
                throw new InvalidOperationException(
                    $"Login non completato: il pool richiede una sfida non gestita ({response.ChallengeName}).");
            }

            _user = user;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetIdTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_user?.SessionTokens is null)
            {
                return null;
            }

            if (!forceRefresh && _user.SessionTokens.IsValid())
            {
                return _user.SessionTokens.IdToken;
            }

            await _user.StartWithRefreshTokenAuthAsync(
                    new InitiateRefreshTokenAuthRequest { AuthFlowType = AuthFlowType.REFRESH_TOKEN_AUTH })
                .ConfigureAwait(false);

            return _user.SessionTokens?.IdToken;
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
        _user?.SignOut();
        _user = null;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _provider.Dispose();
    }
}
