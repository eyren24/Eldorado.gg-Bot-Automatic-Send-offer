using Microsoft.Web.WebView2.Wpf;

namespace EldoradoApp.Services;

/// <summary>
/// The cookies and User-Agent of the app's embedded browser, so plain
/// <see cref="System.Net.Http.HttpClient"/> calls can inherit its session.
/// </summary>
/// <remarks>
/// <c>login.eldorado.gg</c> sits behind a Cloudflare <i>managed challenge</i> that
/// covers the whole zone — <c>/oauth2/authorize</c> and <c>/oauth2/token</c> alike — so
/// a raw HTTP client always gets a 403 challenge page. Only a real browser can solve it,
/// and it records the result in a <c>cf_clearance</c> cookie bound to that browser's
/// User-Agent and IP. Replaying both makes the token exchange go through.
/// </remarks>
public sealed record BrowserContext(string CookieHeader, string UserAgent)
{
    public bool IsUsable => CookieHeader.Length > 0;
}

/// <summary>Reads a <see cref="BrowserContext"/> out of a live WebView2.</summary>
public static class WebViewBrowserContext
{
    public static async Task<BrowserContext?> CaptureAsync(WebView2? browser, string url)
    {
        if (browser?.CoreWebView2 is not { } core)
        {
            return null;
        }

        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(url);
            var header = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

            // The clearance cookie is only accepted together with the UA that earned it.
            var userAgent = core.Settings.UserAgent;

            return new BrowserContext(header, userAgent);
        }
        catch (Exception ex)
        {
            ApiLog.Write($"BrowserContext capture failed: {ex.Message}");
            return null;
        }
    }
}
