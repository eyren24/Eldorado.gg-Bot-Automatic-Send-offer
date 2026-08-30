using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EldoradoApp.Services;
using EldoradoApp.ViewModels;

namespace EldoradoApp.Views.Pages;

/// <summary>
/// The in-app Eldorado browser. It is both where the seller signs in to the website and
/// the channel the bot uses to post the follow-up message, so it is created once and
/// kept alive for the whole session (the shell only hides it when another page is shown).
/// </summary>
/// <remarks>
/// Start-up is deliberately defensive. On Windows builds that ship without WebView2 the
/// runtime check fails, and retrying it — which this page used to do on every visibility
/// change, with a modal error each time — froze the whole app. Now it is attempted at
/// most once, off the critical path, and a missing runtime just shows a panel.
/// </remarks>
public partial class ChatPage : UserControl
{
    private ChatBrowserMessenger? _messenger;
    private bool _attempted;

    public ChatPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!WebViewEnvironment.IsInstalled)
        {
            ShowMissingRuntime();
            return;
        }

        // Warm the browser up once the window is idle: never during layout, never blocking.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await EnsureBrowserAsync()));
    }

    /// <summary>Creates the browser at most once; failures are reported, not retried.</summary>
    private async Task EnsureBrowserAsync()
    {
        if (_attempted || Browser.CoreWebView2 is not null || DataContext is not ShellViewModel shell)
        {
            return;
        }

        _attempted = true;

        try
        {
            var environment = await WebViewEnvironment.GetAsync();
            await Browser.EnsureCoreWebView2Async(environment);
            WebViewEnvironment.KeepPopupsInline(Browser.CoreWebView2!);

            _messenger = new ChatBrowserMessenger(Browser, Dispatcher, () => shell.Host.Settings.Message);
            _messenger.Attach();

            // From now on the bot delivers through this browser; the clipboard stays as fallback.
            shell.Messages.Primary = _messenger;

            // This browser also carries the Eldorado session the API client borrows.
            shell.RegisterSiteTokenReader(() => EldoradoSiteSession.ReadIdTokenAsync(Browser));

            Browser.CoreWebView2!.SourceChanged += (_, _) => AddressBox.Text = Browser.Source?.ToString() ?? "";

            MissingRuntimePanel.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Visible;

            Navigate(shell.Host.Settings.Message.ChatUrl);

            // If the seller was already signed in last time, pick that session straight up.
            await shell.TryAdoptBrowserSessionAsync();
        }
        catch (Exception ex)
        {
            ApiLog.Write($"ChatPage WebView2 init failed: {ex.Message}");
            ShowMissingRuntime(ex is WebViewMissingException ? null : ex.Message);
        }
    }

    /// <summary>Inline explanation — never a modal dialog, which is what locked the UI.</summary>
    private void ShowMissingRuntime(string? detail = null)
    {
        _attempted = true;
        Browser.Visibility = Visibility.Collapsed;
        MissingRuntimePanel.Visibility = Visibility.Visible;

        if (detail is not null)
        {
            MissingRuntimeDetail.Text = $"Dettaglio tecnico: {detail}";
            MissingRuntimeDetail.Visibility = Visibility.Visible;
        }

        if (DataContext is ShellViewModel shell)
        {
            shell.RefreshBrowserStatus();
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _attempted = false;
        MissingRuntimeDetail.Visibility = Visibility.Collapsed;
        _ = EnsureBrowserAsync();
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(WebViewEnvironment.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Could not open the WebView2 download page: {ex.Message}");
            Clipboard.SetText(WebViewEnvironment.DownloadUrl);
            MissingRuntimeDetail.Text = "Link copiato negli appunti: " + WebViewEnvironment.DownloadUrl;
            MissingRuntimeDetail.Visibility = Visibility.Visible;
        }
    }

    private void Navigate(string url)
    {
        if (Browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            Browser.CoreWebView2.Navigate(url);
            AddressBox.Text = url;
        }
        catch (Exception ex)
        {
            ApiLog.Write($"ChatPage navigate failed: {ex.Message}");
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
        {
            Browser.CoreWebView2.GoForward();
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void Go_Click(object sender, RoutedEventArgs e) => Navigate(AddressBox.Text);

    private void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Navigate(AddressBox.Text);
        }
    }

    private void OpenChat_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            Navigate(shell.Host.Settings.Message.ChatUrl);
        }
    }

    /// <summary>
    /// Writes down what the page currently looks like to the chat scripts. The markup is
    /// Eldorado's, so when delivery starts failing this report — not a screenshot — is what
    /// says which step lost the conversation.
    /// </summary>
    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if (_messenger is null)
        {
            MissingRuntimeDetail.Text = "Browser non ancora avviato.";
            MissingRuntimeDetail.Visibility = Visibility.Visible;
            return;
        }

        var button = (Button)sender;
        button.IsEnabled = false;

        try
        {
            var buyer = (DataContext as ShellViewModel)?.Message.History.FirstOrDefault()?.Buyer;
            var report = await _messenger.DiagnoseAsync(buyer);

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EldoradoApp", "chat-diagnostica.json");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, report);

            Clipboard.SetText(path);
            AddressBox.Text = $"Report salvato in {path} (percorso copiato negli appunti)";
        }
        catch (Exception ex)
        {
            ApiLog.Write($"ChatPage diagnostics failed: {ex.Message}");
            AddressBox.Text = $"Diagnostica non riuscita: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
