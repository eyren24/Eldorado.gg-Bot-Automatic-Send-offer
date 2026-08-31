namespace EldoradoApp.ViewModels;

/// <summary>The screens of the single-window shell (no dialogs, no extra windows).</summary>
public enum AppSection
{
    Dashboard,
    Pricing,
    Units,
    Extras,
    Message,
    Chat,
    Account,
    License
}

/// <summary>One entry of the navigation rail.</summary>
public sealed record NavItem(AppSection Section, string Title, string Icon, string Hint);
