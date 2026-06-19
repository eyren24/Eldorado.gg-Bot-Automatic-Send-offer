using System.IO;

namespace EldoradoApp.Services;

/// <summary>
/// Tiny append-only file logger for diagnosing the live API: every request, response and
/// error is written to %AppData%\EldoradoApp\api.log with a timestamp. Best-effort and
/// thread-safe; never throws into the caller.
/// </summary>
public static class ApiLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp", "api.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    /// <summary>Starts a fresh log for a new app session (keeps the file from growing forever).</summary>
    public static void StartSession(string header)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    $"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} · {header} ===={Environment.NewLine}");
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
