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
}
