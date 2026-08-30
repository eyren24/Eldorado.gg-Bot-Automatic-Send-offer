using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;
using Microsoft.Win32;

namespace EldoradoApp.ViewModels;

/// <summary>
/// The message the bot fires the instant an offer is submitted: template with
/// placeholders, the banner that goes with it, how it's delivered, and a live preview
/// rendered with a realistic example offer.
/// </summary>
public sealed partial class MessageViewModel : ObservableObject
{
    private readonly SettingsHost _host;
    private bool _loading;

    private BoostingBotSettings Settings => _host.Settings;
    private OfferMessageSettings Config => Settings.Message;

    public Array Deliveries { get; } = Enum.GetValues(typeof(MessageDelivery));

    /// <summary>Placeholder chips; clicking one appends it to the template.</summary>
    public ObservableCollection<PlaceholderChip> Placeholders { get; } = [];

    /// <summary>Every message the bot has tried to deliver in this session.</summary>
    public ObservableCollection<SentMessageRow> History { get; } = [];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private MessageDelivery _delivery;
    [ObservableProperty] private string _template = "";
    [ObservableProperty] private string _bannerPath = "";
    [ObservableProperty] private bool _copyToClipboard;
    [ObservableProperty] private bool _attachBanner;
    [ObservableProperty] private bool _strictBuyerMatch;
    [ObservableProperty] private string _conversationUrl = "";
    [ObservableProperty] private int _delaySeconds;
    [ObservableProperty] private string _chatUrl = "";
    [ObservableProperty] private int _maxAttempts;
    [ObservableProperty] private string _chatScript = "";

    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _bannerStatus = "Nessun banner selezionato";
    [ObservableProperty] private bool _hasBanner;

    public MessageViewModel(SettingsHost host)
    {
        _host = host;

        foreach (var (token, description) in OfferMessageComposer.Placeholders)
        {
            Placeholders.Add(new PlaceholderChip(token, description));
        }

        Reload();
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            Enabled = Config.Enabled;
            Delivery = Config.Delivery;
            Template = Config.Template;
            BannerPath = Config.BannerPath;
            CopyToClipboard = Config.CopyToClipboard;
            AttachBanner = Config.AttachBanner;
            StrictBuyerMatch = Config.StrictBuyerMatch;
            ConversationUrl = Config.ConversationUrl;
            DelaySeconds = Config.DelaySeconds;
            ChatUrl = Config.ChatUrl;
            MaxAttempts = Config.MaxAttempts;
            ChatScript = Config.ChatScript;
        }
        finally
        {
            _loading = false;
        }

        RefreshPreview();
    }

    /// <summary>Copies the editable fields back into the settings object.</summary>
    public void Apply()
    {
        Config.Enabled = Enabled;
        Config.Delivery = Delivery;
        Config.Template = Template ?? "";
        Config.BannerPath = BannerPath ?? "";
        Config.CopyToClipboard = CopyToClipboard;
        Config.AttachBanner = AttachBanner;
        Config.StrictBuyerMatch = StrictBuyerMatch;
        Config.ConversationUrl = (ConversationUrl ?? "").Trim();
        Config.DelaySeconds = Math.Max(0, DelaySeconds);
        Config.ChatUrl = string.IsNullOrWhiteSpace(ChatUrl) ? OfferMessageSettings.DefaultChatUrl : ChatUrl.Trim();
        Config.MaxAttempts = Math.Clamp(MaxAttempts, 1, 10);
        Config.ChatScript = ChatScript ?? "";
    }

    partial void OnEnabledChanged(bool value) => ApplyAndPreview();
    partial void OnDeliveryChanged(MessageDelivery value) => ApplyAndPreview();
    partial void OnTemplateChanged(string value) => ApplyAndPreview();
    partial void OnBannerPathChanged(string value) => ApplyAndPreview();
    partial void OnCopyToClipboardChanged(bool value) => ApplyAndPreview();
    partial void OnAttachBannerChanged(bool value) => ApplyAndPreview();
    partial void OnStrictBuyerMatchChanged(bool value) => ApplyAndPreview();
    partial void OnConversationUrlChanged(string value) => ApplyAndPreview();
    partial void OnDelaySecondsChanged(int value) => ApplyAndPreview();
    partial void OnChatUrlChanged(string value) => ApplyAndPreview();
    partial void OnMaxAttemptsChanged(int value) => ApplyAndPreview();
    partial void OnChatScriptChanged(string value) => ApplyAndPreview();

    private void ApplyAndPreview()
    {
        if (_loading)
        {
            return;
        }

        Apply();
        RefreshPreview();
        _host.Touch();
    }

    /// <summary>Renders the template against a realistic example offer.</summary>
    public void RefreshPreview()
    {
        HasBanner = !string.IsNullOrWhiteSpace(BannerPath) && File.Exists(BannerPath);
        BannerStatus = string.IsNullOrWhiteSpace(BannerPath)
            ? "Nessun banner selezionato"
            : HasBanner
                ? $"Banner: {Path.GetFileName(BannerPath)}"
                : $"File non trovato: {BannerPath}";

        var ladder = Settings.Pricing.Ladder;
        var from = ladder.Divisions.FirstOrDefault(d => d.StartsWith("Gold", StringComparison.OrdinalIgnoreCase))
                   ?? ladder.Divisions.FirstOrDefault();
        var to = ladder.Divisions.FirstOrDefault(d => d.StartsWith("Platinum", StringComparison.OrdinalIgnoreCase))
                 ?? ladder.Divisions.LastOrDefault();

        var extras = Settings.Extras.Where(e => e.Enabled).Take(1).Select(e => e.Id).ToList();
        var quote = BoostingPriceCalculator.Quote(from, to, extras, Settings);

        var example = new BoostingRequest(
            Id: "preview", GameId: null, BoostingCategoryId: null,
            BoostingCategoryTitle: $"Valorant Rank Boost {from} to {to}",
            BuyerId: null, BuyerUsername: "MarioRossi",
            IsBuyerMuted: false, CreatedDate: DateTimeOffset.Now);

        Preview = OfferMessageComposer.Compose(Template ?? "", example, quote, Settings.DefaultDeliveryTime);
    }

    [RelayCommand]
    private void PickBanner()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Scegli il banner da allegare",
            Filter = "Immagini|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|Tutti i file|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            BannerPath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void ClearBanner() => BannerPath = "";

    [RelayCommand]
    private void InsertPlaceholder(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            Template = (Template ?? "") + token;
        }
    }

    [RelayCommand]
    private void ResetTemplate()
    {
        Template = new OfferMessageSettings().Template;
    }

    [RelayCommand]
    private void ResetScript() => ChatScript = "";

    [RelayCommand]
    private void Save()
    {
        Apply();
        _host.Save();
    }

    /// <summary>Records an attempted delivery in the history list (newest first).</summary>
    public void Record(OfferMessageRecord record)
    {
        History.Insert(0, new SentMessageRow(record));
        while (History.Count > 100)
        {
            History.RemoveAt(History.Count - 1);
        }
    }
}

/// <summary>A clickable {placeholder} chip.</summary>
public sealed record PlaceholderChip(string Token, string Description);
