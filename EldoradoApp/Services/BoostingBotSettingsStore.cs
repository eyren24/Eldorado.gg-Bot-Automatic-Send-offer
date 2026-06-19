using System.IO;
using System.Text.Json;
using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>Loads/saves <see cref="BoostingBotSettings"/> as JSON under %AppData%\EldoradoApp.</summary>
public static class BoostingBotSettingsStore
{
    private static readonly string Directory_ =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldoradoApp");

    private static readonly string FilePath = Path.Combine(Directory_, "boosting-bot.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static BoostingBotSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<BoostingBotSettings>(File.ReadAllText(FilePath));
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable → safe defaults (disarmed, dry-run).
        }

        return BoostingBotSettings.CreateDefault();
    }

    public static void Save(BoostingBotSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Directory_);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Non-fatal.
        }
    }
}
