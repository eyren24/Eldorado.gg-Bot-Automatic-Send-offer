using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EldoradoApp.Services;
using EldoradoApp.ViewModels;

namespace EldoradoApp;

public partial class App : Application
{
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp", "crash.log");

    /// <summary>
    /// Live Seller API backend (auth + orders + boosting requests + auto-offer bot).
    /// The market monitor is a work-in-progress placeholder; the bot is the live feature.
    /// </summary>
    public EldoradoBackend Backend { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Persist unhandled exceptions so issues on a client's machine can be diagnosed.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogCrash(ev.ExceptionObject as Exception);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var feed = new RequestsFeedViewModel(Backend);
        var window = new MainWindow { DataContext = feed };
        window.Loaded += async (_, _) =>
        {
            // Pick up stored seller credentials, if any, so live calls are ready.
            var signedIn = await Backend.TrySignInFromStoreAsync();
            Debug.WriteLine($"[Eldorado] Seller API signed in: {signedIn}");
            await feed.InitializeAsync();
        };

        window.Show();
    }

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
