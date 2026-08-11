using System.ComponentModel;
using System.Web;
using System.Windows;
using EldoradoApp.Services;
using EldoradoApp.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace EldoradoApp;

/// <summary>
/// The application shell. Everything lives here: navigation only swaps which panel is
/// visible, and the OAuth sign-in runs in an overlay browser inside this same window —
/// no second window is ever created.
/// </summary>
public partial class MainWindow : Window
{
    private ShellViewModel? _shell;
    private bool _loginBrowserReady;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }

        _shell = DataContext as ShellViewModel;
        if (_shell is null)
        {
            return;
        }

        _shell.PropertyChanged += OnShellPropertyChanged;

        // The "is Google enabled?" check needs a real browser: Cloudflare challenges the
        // whole login.eldorado.gg zone, so no HttpClient/script can read the answer.
        _shell.GoogleProbe = async (url, token) =>
        {
            await EnsureLoginBrowserAsync();
            return await new GoogleAuthProbe(LoginBrowser, Dispatcher).RunAsync(url, token);
        };

        // Same reason: let the token exchange borrow the browser's Cloudflare clearance.
        if (Application.Current is App app)
        {
            app.Backend.OAuth.BrowserContextProvider =
                () => WebViewBrowserContext.CaptureAsync(LoginBrowser, EldoradoApiOptions.HostedUiDomain);
        }

        // The overlay browser is where sign-in happens, so it reads the session too.
        _shell.RegisterSiteTokenReader(() => EldoradoSiteSession.ReadIdTokenAsync(LoginBrowser));
    }

    private async void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // During a probe the prober drives navigation itself; don't send the browser twice.
        if (e.PropertyName == nameof(ShellViewModel.IsLoginVisible) &&
            _shell is { IsLoginVisible: true, IsCheckingGoogle: false })
        {
            await ShowLoginBrowserAsync(_shell.LoginUrl);
        }
    }

    /// <summary>Creates the overlay browser once, sharing the app's WebView2 profile.</summary>
    private async Task EnsureLoginBrowserAsync()
    {
        if (_loginBrowserReady)
        {
            return;
        }

        var environment = await WebViewEnvironment.GetAsync();
        await LoginBrowser.EnsureCoreWebView2Async(environment);
        WebViewEnvironment.KeepPopupsInline(LoginBrowser.CoreWebView2);
        _loginBrowserReady = true;
    }

    /// <summary>Boots the overlay browser (once) and points it at the authorize URL.</summary>
    private async Task ShowLoginBrowserAsync(string authorizeUrl)
    {
        try
        {
            await EnsureLoginBrowserAsync();
            LoginBrowser.CoreWebView2.Navigate(authorizeUrl);
        }
        catch (Exception ex)
        {
            // Never a modal dialog here: on machines without WebView2 this path can fire
            // repeatedly, and stacked dialogs make the app look frozen.
            ApiLog.Write($"Login overlay failed: {ex.Message}");
            if (_shell is not null)
            {
                _shell.IsLoginVisible = false;
                _shell.IsSiteLogin = false;
                _shell.RefreshBrowserStatus();
                _shell.StatusMessage = ex is WebViewMissingException
                    ? "WebView2 non installato: accesso dal sito non disponibile. Usa email e password o il token."
                    : $"Browser integrato non disponibile: {ex.Message}";
            }
        }
    }
}
