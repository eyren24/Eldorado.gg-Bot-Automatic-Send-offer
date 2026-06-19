using System.Diagnostics;
using System.Net.Http;

namespace EldoradoApp.Services;

/// <summary>
/// Logs every outgoing API call — method, URL, whether an auth cookie was attached,
/// the response status/elapsed time and a truncated body, plus any exception — to
/// <see cref="ApiLog"/>. Sits inside the auth handler so it sees the final request.
/// </summary>
public sealed class ApiLoggingHandler : DelegatingHandler
{
    private const int MaxBody = 1500;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var hasCookie = request.Headers.Contains("Cookie");
        ApiLog.Write($"→ {request.Method} {request.RequestUri}  [auth cookie: {(hasCookie ? "yes" : "NO")}]");

        if (request.Content is not null && request.Method != HttpMethod.Get)
        {
            try
            {
                var reqBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                ApiLog.Write($"   body: {Truncate(reqBody)}");
            }
            catch
            {
                // Ignore unreadable request bodies.
            }
        }

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ApiLog.Write($"✖ {request.Method} {request.RequestUri} threw after {stopwatch.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        stopwatch.Stop();

        string body;
        try
        {
            // Response content is buffered by default, so reading here doesn't disturb the caller.
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            body = "(unreadable)";
        }

        var status = (int)response.StatusCode;
        var marker = response.IsSuccessStatusCode ? "←" : "←✖";
        ApiLog.Write($"{marker} {status} {response.ReasonPhrase} ({stopwatch.ElapsedMilliseconds} ms)  body: {Truncate(body)}");

        return response;
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var oneLine = value.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= MaxBody ? oneLine : oneLine[..MaxBody] + $"… (+{oneLine.Length - MaxBody} chars)";
    }
}
