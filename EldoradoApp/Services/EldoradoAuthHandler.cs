using System.Net;
using System.Net.Http;

namespace EldoradoApp.Services;

/// <summary>
/// Attaches the Cognito IdToken as the <c>__Host-EldoradoIdToken</c> cookie on
/// every outgoing request. On a 401/403 it forces a token refresh and retries
/// the request once.
/// </summary>
public sealed class EldoradoAuthHandler(IEldoradoTokenProvider auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await ApplyTokenAsync(request, forceRefresh: false, cancellationToken).ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && auth.IsSignedIn)
        {
            var retry = await CloneAsync(request).ConfigureAwait(false);
            await ApplyTokenAsync(retry, forceRefresh: true, cancellationToken).ConfigureAwait(false);

            // Only resend if we actually obtained a (possibly refreshed) token.
            if (retry.Headers.Contains("Cookie"))
            {
                response.Dispose();
                response = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
            }
        }

        return response;
    }

    private async Task ApplyTokenAsync(HttpRequestMessage request, bool forceRefresh, CancellationToken cancellationToken)
    {
        var token = await auth.GetIdTokenAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        request.Headers.Remove("Cookie");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{EldoradoApiOptions.IdTokenCookieName}={token}");
        }
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        if (request.Content is not null)
        {
            var buffer = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(buffer);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
