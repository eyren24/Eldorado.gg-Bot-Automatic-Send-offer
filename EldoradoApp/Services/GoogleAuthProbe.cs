using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace EldoradoApp.Services;

public enum GoogleAuthState
{
    /// <summary>The page never settled into a state we recognise.</summary>
    Unknown,

    /// <summary>Cognito answers "Login pages unavailable" — no Hosted UI on the bot client.</summary>
    Disabled,

    /// <summary>The authorize call reaches Google: federated sign-in is live.</summary>
    EnabledGoogle,

    /// <summary>Cognito serves its own managed login page (OAuth on, Google unverified).</summary>
    EnabledLoginPage,

    /// <summary>Cloudflare never let the page through.</summary>
    Blocked,

    Error
}

public sealed record GoogleAuthVerdict(GoogleAuthState State, string Message, string FinalUrl)
{
    public bool IsEnabled => State is GoogleAuthState.EnabledGoogle or GoogleAuthState.EnabledLoginPage;
}

/// <summary>
/// Answers "has Eldorado switched on Google sign-in for the bot client?" from inside the
/// app.
/// </summary>
/// <remarks>
/// It has to run in the embedded browser: the whole <c>login.eldorado.gg</c> zone is
/// behind a Cloudflare managed challenge, so <c>curl</c>, <c>HttpClient</c> and
/// <c>scripts/check_google_auth.py</c> all get a 403 challenge page and can only report
/// "blocked". A real browser solves the challenge and shows the actual answer.
/// </remarks>
public sealed class GoogleAuthProbe(WebView2 browser, Dispatcher dispatcher)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private const string ReadPageScript = """
        JSON.stringify({
          href: location.href,
          title: document.title || '',
          text: (document.body ? document.body.innerText : '').slice(0, 1200)
        })
        """;

    public async Task<GoogleAuthVerdict> RunAsync(string authorizeUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            await dispatcher.InvokeAsync(() => browser.CoreWebView2!.Navigate(authorizeUrl));
        }
        catch (Exception ex)
        {
            return new GoogleAuthVerdict(GoogleAuthState.Error, $"navigazione fallita: {ex.Message}", authorizeUrl);
        }

        var deadline = DateTimeOffset.UtcNow + Timeout;
        var lastUrl = authorizeUrl;
        var sawChallenge = false;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var page = await ReadPageAsync().ConfigureAwait(false);
            if (page is null)
            {
                continue;
            }

            lastUrl = page.Value.Href;
            var verdict = Classify(page.Value.Href, page.Value.Title, page.Value.Text);

            if (verdict is not null)
            {
                return verdict;
            }

            if (IsChallenge(page.Value.Title, page.Value.Text))
            {
                sawChallenge = true;
            }
        }

        return sawChallenge
            ? new GoogleAuthVerdict(GoogleAuthState.Blocked,
                "Cloudflare non ha completato la verifica: riprova, o apri la pagina dalla scheda Chat.", lastUrl)
            : new GoogleAuthVerdict(GoogleAuthState.Unknown,
                "La pagina non ha dato una risposta riconoscibile — controllala a mano.", lastUrl);
    }

    /// <summary>Returns a verdict as soon as the page says something conclusive, else null.</summary>
    private static GoogleAuthVerdict? Classify(string href, string title, string text)
    {
        if (href.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return new GoogleAuthVerdict(GoogleAuthState.EnabledGoogle,
                "ATTIVO: l'authorize porta alla schermata Google. Il login Google è utilizzabile.", href);
        }

        if (href.StartsWith(EldoradoApiOptions.OAuthRedirectUri, StringComparison.OrdinalIgnoreCase))
        {
            return new GoogleAuthVerdict(GoogleAuthState.EnabledGoogle,
                "ATTIVO: l'authorize è tornato subito alla callback (sessione già valida).", href);
        }

        if (text.Contains("Login pages unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return new GoogleAuthVerdict(GoogleAuthState.Disabled,
                "NON ATTIVO: Cognito risponde «Login pages unavailable» — Eldorado non ha abilitato la Hosted UI sul client bot. Usa email+password o il token.",
                href);
        }

        if (href.Contains("/error", StringComparison.OrdinalIgnoreCase) &&
            href.Contains("login.eldorado.gg", StringComparison.OrdinalIgnoreCase))
        {
            return new GoogleAuthVerdict(GoogleAuthState.Disabled,
                $"NON ATTIVO: la Hosted UI ha risposto con un errore ({Snippet(text)}).", href);
        }

        if (IsChallenge(title, text))
        {
            return null; // Cloudflare is still working; keep waiting.
        }

        var looksLikeLogin = text.Contains("password", StringComparison.OrdinalIgnoreCase)
                             || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
                             || text.Contains("accedi", StringComparison.OrdinalIgnoreCase);

        if (looksLikeLogin && href.Contains("login.eldorado.gg", StringComparison.OrdinalIgnoreCase))
        {
            return new GoogleAuthVerdict(GoogleAuthState.EnabledLoginPage,
                "ATTIVO (parziale): la Hosted UI serve una pagina di login. Verifica se c'è il pulsante Google.",
                href);
        }

        return null;
    }

    private static bool IsChallenge(string title, string text) =>
        title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Ci siamo quasi", StringComparison.OrdinalIgnoreCase)
        || text.Contains("verifica di sicurezza", StringComparison.OrdinalIgnoreCase)
        || text.Contains("security check", StringComparison.OrdinalIgnoreCase);

    private async Task<(string Href, string Title, string Text)?> ReadPageAsync()
    {
        try
        {
            var json = await dispatcher
                .InvokeAsync(() => browser.CoreWebView2!.ExecuteScriptAsync(ReadPageScript))
                .Task.Unwrap()
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json) || json == "null")
            {
                return null;
            }

            // ExecuteScript returns the JS value JSON-encoded, so our string is double-encoded.
            var inner = JsonSerializer.Deserialize<string>(json);
            if (inner is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(inner);
            var root = document.RootElement;
            return (
                root.GetProperty("href").GetString() ?? "",
                root.GetProperty("title").GetString() ?? "",
                root.GetProperty("text").GetString() ?? "");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Snippet(string text)
    {
        var oneLine = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return oneLine.Length <= 120 ? oneLine : oneLine[..120] + "…";
    }
}
