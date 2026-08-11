using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using EldoradoApp.Services;
using EldoradoApp.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace EldoradoApp;

public partial class App : Application
{
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp", "crash.log");

    /// <summary>Live Seller API backend (auth + boosting requests + auto-offer bot).</summary>
    public EldoradoBackend Backend { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF bindings default to en-US regardless of the machine's locale, which would
        // make every price box reject "0,25" and print "0.25". Bind them to the real
        // culture instead — this app is full of decimal inputs.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        // Persist unhandled exceptions so issues on a client's machine can be diagnosed.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogCrash(ev.ExceptionObject as Exception);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Safe mode for stripped-down Windows builds without WebView2.
        if (e.Args.Any(a => string.Equals(a, "--no-browser", StringComparison.OrdinalIgnoreCase)))
        {
            WebViewEnvironment.Disable();
        }

        if (e.Args.Any(a => string.Equals(a, "--check-google", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunGoogleCheckAsync();
            return;
        }

        var shell = new ShellViewModel(Backend);
        var window = new MainWindow { DataContext = shell };

        // Restoring the session touches the network, so do it once the shell is on screen.
        window.Loaded += async (_, _) => await shell.InitializeAsync();

        window.Show();
    }

    /// <summary>
    /// Headless check (<c>EldoradoApp.exe --check-google</c>): reports whether Eldorado
    /// has enabled the Cognito Hosted UI / Google sign-in on the bot client, and writes
    /// the verdict to <see cref="GoogleCheckReport"/>.
    /// </summary>
    /// <remarks>
    /// It has to drive a real browser. The whole <c>login.eldorado.gg</c> zone sits behind
    /// a Cloudflare managed challenge, so <c>curl</c>, <c>HttpClient</c> and the Python
    /// probe in <c>scripts/</c> can only ever report "blocked by Cloudflare".
    /// </remarks>
    private async Task RunGoogleCheckAsync()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!WebViewEnvironment.IsInstalled)
        {
            await WriteGoogleReportAsync(
                "Verifica non eseguibile: il runtime Microsoft WebView2 non è installato su questo PC.\n" +
                $"Installalo da {WebViewEnvironment.DownloadUrl} (Evergreen Standalone Installer) e riprova.\n\n" +
                "Nota: il controllo richiede un browser vero perché Cloudflare blocca ogni client automatico " +
                "su login.eldorado.gg.");
            Shutdown();
            return;
        }

        // WebView2 needs a real HWND; park it off-screen so the check stays unattended.
        var window = new Window
        {
            Title = "Verifica accesso Google",
            Width = 900,
            Height = 700,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow
        };

        var browser = new WebView2();
        window.Content = browser;
        window.Show();

        string report;
        try
        {
            await browser.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());

            var (authorizeUrl, _) = Backend.OAuth.BeginSignIn();
            var verdict = await new GoogleAuthProbe(browser, Dispatcher).RunAsync(authorizeUrl);
            var loginPage = await EldoradoSiteSession.CheckLoginPageAsync(browser, Dispatcher);

            report = $"""
                Eldorado · verifica accesso
                Client bot : {EldoradoApiOptions.ClientId}
                Hosted UI  : {EldoradoApiOptions.AuthorizeEndpoint}
                Eseguita   : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}

                GOOGLE     : {verdict.State}
                {verdict.Message}
                URL finale : {verdict.FinalUrl}

                PAGINA DI ACCESSO (usata dal pulsante «Accedi a Eldorado»)
                {loginPage}
                """;
        }
        catch (Exception ex)
        {
            report = $"Verifica fallita: {ex.Message}";
        }
        finally
        {
            window.Close();
        }

        await WriteGoogleReportAsync(report);
        Shutdown();
    }

    /// <summary>Saves the diagnostic verdict where support can find it.</summary>
    private static async Task WriteGoogleReportAsync(string report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoogleCheckReport)!);
            // BOM so Notepad and PowerShell don't mangle the accented text.
            await File.WriteAllTextAsync(GoogleCheckReport, report, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Google check report not written: {ex.Message}");
        }

        ApiLog.Write(report.Replace(Environment.NewLine, " | "));
    }

    /// <summary>Where <c>--check-google</c> leaves its verdict.</summary>
    public static string GoogleCheckReport { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EldoradoApp", "google-auth-check.txt");

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        MessageBox.Show(
            $"Si è verificato un errore:\n\n{e.Exception.Message}\n\nDettagli salvati in:\n{CrashLog}",
            "EldoradoApp", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging itself crash the app.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Backend.Dispose();
        base.OnExit(e);
    }
}
