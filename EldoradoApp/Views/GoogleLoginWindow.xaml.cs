using System.IO;
using System.Web;
using System.Windows;
using EldoradoApp.Services;
using Microsoft.Web.WebView2.Core;

namespace EldoradoApp.Views;

/// <summary>
/// Hosts the Cognito Hosted UI in a WebView2 so the seller can sign in (incl. Google).
/// Watches for the redirect back to <see cref="EldoradoApiOptions.OAuthRedirectUri"/> and
/// captures the <c>?code=…</c>. Show with <see cref="Window.ShowDialog"/>; a <c>true</c>
/// result means <see cref="AuthorizationCode"/> is set.
/// </summary>
public partial class GoogleLoginWindow : Window
{
    private readonly string _authorizeUrl;

    public string? AuthorizationCode { get; private set; }
    public string? Error { get; private set; }

    public GoogleLoginWindow(string authorizeUrl)
    {
        _authorizeUrl = authorizeUrl;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Keep WebView2's profile in a writable per-user folder (the exe dir may be read-only).
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EldoradoApp", "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await Web.EnsureCoreWebView2Async(environment);

            Web.NavigationStarting += OnNavigationStarting;
            Web.Source = new Uri(_authorizeUrl);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            MessageBox.Show(
                $"Impossibile avviare il login nel browser integrato.\n\n{ex.Message}\n\n" +
                "Assicurati che il runtime WebView2 di Microsoft sia installato.",
                "Login Google", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var isCallback = e.Uri.StartsWith(EldoradoApiOptions.OAuthRedirectUri, StringComparison.OrdinalIgnoreCase);

        // Cognito sends OAuth failures (e.g. redirect_mismatch) to its own /error page; capture
        // those too so the reason surfaces instead of a silent cancel.
        var isHostedUiError = e.Uri.StartsWith(
            $"{EldoradoApiOptions.HostedUiDomain}/error", StringComparison.OrdinalIgnoreCase);

        if (!isCallback && !isHostedUiError)
        {
            return;
        }

        // Don't actually load the callback/error page; just harvest the code/error from its query.
        e.Cancel = true;

        var query = HttpUtility.ParseQueryString(new Uri(e.Uri).Query);
        AuthorizationCode = query["code"];
        Error = query["error_description"] ?? query["error"];

        DialogResult = AuthorizationCode is not null;
    }
}
