using EldoradoApp.Models;

namespace EldoradoApp.Services;

/// <summary>
/// The one live copy of the bot settings, shared by every screen. Each page edits the
/// same object and calls <see cref="Save"/>; <see cref="Changed"/> then lets the other
/// pages (feed prices, message preview, …) refresh without knowing about each other.
/// </summary>
public sealed class SettingsHost
{
    public BoostingBotSettings Settings { get; private set; } = BoostingBotSettingsStore.Load();

    /// <summary>Raised after the settings were saved or reloaded.</summary>
    public event Action? Changed;

    public void Save()
    {
        Settings.Normalized();
        BoostingBotSettingsStore.Save(Settings);
        Changed?.Invoke();
    }

    public void Reload()
    {
        Settings = BoostingBotSettingsStore.Load();
        Changed?.Invoke();
    }

    /// <summary>Notifies listeners without writing to disk (live preview while typing).</summary>
    public void Touch() => Changed?.Invoke();
}
