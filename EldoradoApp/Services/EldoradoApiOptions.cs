namespace EldoradoApp.Services;

/// <summary>
/// Public configuration for the official Eldorado Seller API.
/// These are the public Cognito app parameters published in the seller API docs
/// (not secrets). Authentication is USER_SRP_AUTH (email + password) and the
/// resulting IdToken is sent on private endpoints as the
/// <c>__Host-EldoradoIdToken</c> cookie.
/// </summary>
/// <remarks>
/// A bot must be authorized by Eldorado (email api@eldorado.gg) before private
/// endpoints will accept its token.
/// </remarks>
public static class EldoradoApiOptions
{
    public const string UserPoolId = "us-east-2_MlnzCFgHk";
    public const string ClientId = "1956req5ro9drdtbf5i6kis4la";
    public const string Region = "us-east-2";

    /// <summary>Base address for REST calls. The Swagger server entry.</summary>
    public const string BaseUrl = "https://www.eldorado.gg/";

    /// <summary>Cookie name carrying the Cognito IdToken on private endpoints.</summary>
    public const string IdTokenCookieName = "__Host-EldoradoIdToken";

    // ---- Cognito Hosted UI (OAuth Authorization-Code + PKCE, used for "Sign in with Google") ----

    /// <summary>Cognito Hosted UI domain (authorize/token endpoints).</summary>
    public const string HostedUiDomain = "https://login.eldorado.gg";

    /// <summary>
    /// OAuth redirect the Hosted UI sends the authorization code back to. Must match a
    /// callback URL registered on the bot client EXACTLY. Note: the registered value uses
    /// the <c>www.</c> host (the seller docs' non-www example yields <c>redirect_mismatch</c>).
    /// </summary>
    public const string OAuthRedirectUri = "https://www.eldorado.gg/account/auth-callback";

    /// <summary>
    /// OAuth scopes requested for the IdToken. The bot client only allows <c>openid</c>
    /// (requesting <c>email</c>/<c>profile</c> yields <c>invalid_scope</c>); <c>openid</c>
    /// alone is enough to obtain a valid IdToken for the API cookie.
    /// </summary>
    public const string OAuthScopes = "openid";

    /// <summary>
    /// Cognito federated IdP to go straight to, skipping the managed login page (which is
    /// currently "unavailable" for this client). The user signs into Eldorado via Google.
    /// </summary>
    public const string OAuthIdentityProvider = "Google";

    public static string AuthorizeEndpoint => $"{HostedUiDomain}/oauth2/authorize";
    public static string TokenEndpoint => $"{HostedUiDomain}/oauth2/token";
}
