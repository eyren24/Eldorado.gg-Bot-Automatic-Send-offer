using System.IO;
using Microsoft.Web.WebView2.Core;

namespace EldoradoApp.Services;

/// <summary>
/// One shared WebView2 environment for the whole app, plus the checks that keep a
/// machine <i>without</i> the runtime usable.
/// </summary>
/// <remarks>
/// Both browsers (the sign-in overlay and the chat tab) must use the same user-data
/// folder — WebView2 refuses two environments over one folder in a single process — and
/// sharing it also means signing in once is enough for both.
/// <para>
/// The runtime is absent more often than you'd think: stripped-down Windows builds
/// (Kirby OS, AtlasOS, Ghost Spectre…) remove Edge and WebView2 outright. Creating the
/// environment there is slow and throws, so availability is probed up front with
/// <see cref="CoreWebView2Environment.GetAvailableBrowserVersionString(string)"/> — which
/// is a cheap registry lookup — and the answer is cached. The app must stay fully
/// operational without it: only the chat tab, the in-app sign-in and the automatic
/// message need a browser.
/// </para>
/// </remarks>
public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _environment;
    private static bool? _isInstalled;

    /// <summary>Where to get the runtime when it's missing.</summary>
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp", "WebView2");

    /// <summary>Installed runtime version, or null when WebView2 isn't on this machine.</summary>
    public static string? RuntimeVersion { get; private set; }

    /// <summary>
    /// Turns the embedded browser off for the whole session (<c>--no-browser</c>): a safe
    /// mode for machines where WebView2 is absent or misbehaving, and the way to reproduce
    /// that setup on a PC that does have it.
    /// </summary>
    public static void Disable()
    {
        _isInstalled = false;
        RuntimeVersion = null;
        ApiLog.Write("WebView2 disabled by --no-browser.");
    }

    /// <summary>
    /// Whether WebView2 is usable here. Checked once and remembered: a missing runtime
    /// must never be re-probed on every navigation or the UI crawls.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            if (_isInstalled is { } cached)
            {
                return cached;
            }

            try
            {
                RuntimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
                _isInstalled = !string.IsNullOrWhiteSpace(RuntimeVersion);
            }
            catch (Exception ex)
            {
                ApiLog.Write($"WebView2 runtime not available: {ex.Message}");
                RuntimeVersion = null;
                _isInstalled = false;
            }

            return _isInstalled.Value;
        }
    }

    /// <summary>Human-readable status for the UI.</summary>
    public static string StatusText => IsInstalled
        ? $"Browser integrato pronto (WebView2 {RuntimeVersion})"
        : "WebView2 non installato: chat, accesso dal sito e invio automatico del messaggio non sono disponibili.";

    /// <summary>
    /// The shared environment. Throws <see cref="WebViewMissingException"/> when the
    /// runtime isn't installed, so callers fail fast instead of hanging.
    /// </summary>
    public static Task<CoreWebView2Environment> GetAsync()
    {
        if (!IsInstalled)
        {
            throw new WebViewMissingException();
        }

        if (_environment is null)
        {
            Directory.CreateDirectory(UserDataFolder);
            _environment = CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
        }

        return _environment;
    }

    /// <summary>
    /// Keeps pop-ups inside the same view. Sign-in flows routinely call
    /// <c>window.open</c> (Google does), and WebView2 silently drops those windows by
    /// default — which looks like a login button that does nothing.
    /// </summary>
    public static void KeepPopupsInline(CoreWebView2 core)
    {
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
    }
}

/// <summary>Raised when a feature needs the WebView2 runtime and it isn't installed.</summary>
public sealed class WebViewMissingException()
    : InvalidOperationException(
        "Il runtime Microsoft WebView2 non è installato su questo PC. " +
        $"Scaricalo da {WebViewEnvironment.DownloadUrl} (Evergreen Standalone Installer) e riapri l'app.");
