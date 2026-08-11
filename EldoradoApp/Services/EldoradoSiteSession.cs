using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EldoradoApp.Services;

/// <summary>
/// Signs the bot in by borrowing the session of the app's embedded browser: the seller
/// logs into eldorado.gg exactly as they would in Chrome, and the site's own
/// <c>__Host-EldoradoIdToken</c> cookie is then used as the API token.
/// </summary>
/// <remarks>
/// This replaces doing the OAuth code→token exchange ourselves. That exchange targets
/// <c>login.eldorado.gg/oauth2/token</c>, and Cloudflare runs a managed challenge over
/// that whole zone, so it is refused for any non-browser client — which is what the
/// "Cloudflare ha negato lo scambio token" failure was. Letting the website complete its
/// own login sidesteps the problem entirely, and works for Google, email/password and
/// any future method Eldorado adds.
/// </remarks>
public static class EldoradoSiteSession
{
    /// <summary>
    /// Where the seller starts the sign-in: Eldorado's own site, where they press Login
    /// and pick Google or email exactly as in a normal browser.
    /// </summary>
    /// <remarks>
    /// Deliberately the home page. Eldorado has no <c>/login</c> route (it 404s) — the
    /// login is a client-side dialog — and building the Cognito authorize URL ourselves
    /// doesn't work either: the site's <c>/account/auth-callback</c> only accepts a code
    /// paired with the <c>state</c> it generated itself, so a self-started flow ends in
    /// HTTP 400. Letting the site run its own login avoids both traps.
    /// </remarks>
    public const string LoginUrl = "https://www.eldorado.gg/";

    /// <summary>
    /// The origin the session cookie belongs to. The trailing slash matters: cookie
    /// lookups match on path, and a <c>__Host-</c> cookie is pinned to "/".
    /// </summary>
    public const string SiteUrl = "https://www.eldorado.gg/";

    /// <summary>
    /// The IdToken currently held by the browser, from its cookies or — if the site keeps
    /// it there instead — from the page's local/session storage.
    /// </summary>
    public static async Task<string?> ReadIdTokenAsync(WebView2? browser)
    {
        return await ReadFromCookiesAsync(browser) ?? await ReadFromStorageAsync(browser);
    }

    private static async Task<string?> ReadFromCookiesAsync(WebView2? browser)
    {
        if (browser?.CoreWebView2 is not { } core)
        {
            return null;
        }

        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(SiteUrl);

            // Prefer the documented name, but accept any *IdToken* cookie: Eldorado could
            // rename it, and a silent null here looks like "the login button does nothing".
            var token =
                Pick(cookies, c => string.Equals(c.Name, EldoradoApiOptions.IdTokenCookieName, StringComparison.OrdinalIgnoreCase))
                ?? Pick(cookies, c => c.Name.Contains("IdToken", StringComparison.OrdinalIgnoreCase));

            if (token is null)
            {
                ApiLog.Write("Site session: no IdToken cookie. Cookies present: " +
                             (cookies.Count == 0 ? "(nessuno)" : string.Join(", ", cookies.Select(c => c.Name))));
            }

            return token;
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Site session read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Second route to the session: some builds of the site keep the Cognito token in
    /// <c>localStorage</c>/<c>sessionStorage</c> rather than a readable cookie. Only runs
    /// when the browser is actually on eldorado.gg — storage is per-origin.
    /// </summary>
    private static async Task<string?> ReadFromStorageAsync(WebView2? browser)
    {
        if (browser?.CoreWebView2 is not { } core ||
            core.Source?.Contains("eldorado.gg", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        try
        {
            var raw = await core.ExecuteScriptAsync(StorageScanScript);
            if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            {
                return null;
            }

            // ExecuteScript hands back the JS string JSON-encoded, so unwrap twice.
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(inner))
            {
                return null;
            }

            var candidates = JsonSerializer.Deserialize<List<StorageEntry>>(inner) ?? [];

            // Storage is full of JWT-shaped analytics ids; only a genuine Cognito IdToken
            // for Eldorado's user pool may be used, or every API call would 401.
            var hit = candidates.FirstOrDefault(c => IsEldoradoIdToken(c.Value));

            if (hit is not null)
            {
                ApiLog.Write($"Site session: IdToken found in storage under \"{hit.Key}\".");
            }
            else if (candidates.Count > 0)
            {
                ApiLog.Write("Site session: storage has JWT-shaped values but none is an Eldorado IdToken " +
                             $"({string.Join(", ", candidates.Select(c => c.Key))}).");
            }

            return hit?.Value;
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Site storage read failed: {ex.Message}");
            return null;
        }
    }

    private sealed record StorageEntry(string Key, string Value);

    /// <summary>Collects every JWT-shaped value in storage, newest Cognito ones first.</summary>
    private const string StorageScanScript = """
        (function () {
          var found = [];
          var scan = function (store) {
            try {
              for (var i = 0; i < store.length; i++) {
                var key = store.key(i);
                var value = store.getItem(key);
                if (value && value.split('.').length === 3 && value.length > 100) {
                  found.push({ Key: key, Value: value });
                }
              }
            } catch (e) { }
          };
          scan(window.localStorage);
          scan(window.sessionStorage);
          return JSON.stringify(found);
        })();
        """;

    /// <summary>First cookie matching <paramref name="match"/> whose value looks like a JWT.</summary>
    private static string? Pick(IReadOnlyList<CoreWebView2Cookie> cookies, Func<CoreWebView2Cookie, bool> match)
    {
        var value = cookies.FirstOrDefault(c => match(c) && LooksLikeJwt(c.Value))?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool LooksLikeJwt(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Split('.').Length == 3;

    /// <summary>
    /// True only for a Cognito IdToken issued by Eldorado's user pool and still valid.
    /// </summary>
    public static bool IsEldoradoIdToken(string? jwt)
    {
        if (!LooksLikeJwt(jwt))
        {
            return false;
        }

        try
        {
            var payload = jwt!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;

            var issuer = root.TryGetProperty("iss", out var iss) ? iss.GetString() ?? "" : "";
            if (!issuer.Contains(EldoradoApiOptions.UserPoolId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Only the IdToken is accepted as the API cookie; an access token won't do.
            if (root.TryGetProperty("token_use", out var use) &&
                !string.Equals(use.GetString(), "id", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !root.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds) ||
                   DateTimeOffset.FromUnixTimeSeconds(seconds) > DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the sign-in page and reports what came back — the diagnostic behind
    /// <c>EldoradoApp.exe --check-google</c>. A non-200 here is exactly the
    /// "HTTP ERROR 400/404" the seller would see in the overlay.
    /// </summary>
    public static async Task<string> CheckLoginPageAsync(WebView2 browser, Dispatcher dispatcher)
    {
        if (browser.CoreWebView2 is not { } core)
        {
            return "browser non inizializzato";
        }

        // Land on a blank page first: any navigation still in flight would otherwise
        // report its own cancellation as if it were our result.
        await dispatcher.InvokeAsync(() => core.Navigate("about:blank"));
        await Task.Delay(TimeSpan.FromSeconds(1));

        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;

        void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (navigationId is null && e.Uri.StartsWith(SiteUrl, StringComparison.OrdinalIgnoreCase))
            {
                navigationId = e.NavigationId;
            }
        }

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // Only our own top-level navigation counts (redirects keep the same id).
            if (navigationId is not null && e.NavigationId == navigationId)
            {
                completed.TrySetResult(e);
            }
        }

        core.NavigationStarting += OnStarting;
        core.NavigationCompleted += OnCompleted;
        try
        {
            await dispatcher.InvokeAsync(() => core.Navigate(LoginUrl));

            var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(45)));
            if (finished != completed.Task)
            {
                return $"{LoginUrl} — nessuna risposta entro 45s";
            }

            var result = await completed.Task;
            var status = result.HttpStatusCode;
            var token = await ReadIdTokenAsync(browser);
            var session = token is null ? "nessuna sessione salvata" : "sessione già presente ✓";

            return result.IsSuccess
                ? $"{LoginUrl} — HTTP {status} OK · {session}"
                : $"{LoginUrl} — HTTP {status}, errore {result.WebErrorStatus} · {session}";
        }
        finally
        {
            core.NavigationStarting -= OnStarting;
            core.NavigationCompleted -= OnCompleted;
        }
    }
}
