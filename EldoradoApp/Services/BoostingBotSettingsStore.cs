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
                    return Migrate(settings).Normalized();
                }
            }
        }
        catch
        {
            // Corrupt/unreadable → safe defaults (disarmed, dry-run).
        }

        return BoostingBotSettings.CreateDefault().Normalized();
    }

    /// <summary>
    /// Brings a file written by the per-unit pricing build forward: its per-category
    /// prices become flat-price fallbacks. (A legacy file has no <c>pricing</c> section
    /// at all, so the property initialiser already leaves the default ladder in place.)
    /// </summary>
    private static BoostingBotSettings Migrate(BoostingBotSettings settings)
    {
        foreach (var category in settings.CategoryPrices ?? [])
        {
            if (category.FlatPrice <= 0 && category.PricePerUnit > 0)
            {
                category.FlatPrice = category.PricePerUnit;
            }
        }

        // The chat used to live on /chat; the seller inbox is the real destination.
        if (settings.Message is { } message &&
            message.ChatUrl.Contains("eldorado.gg/chat", StringComparison.OrdinalIgnoreCase))
        {
            message.ChatUrl = OfferMessageSettings.DefaultChatUrl;
        }

        return settings;
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
