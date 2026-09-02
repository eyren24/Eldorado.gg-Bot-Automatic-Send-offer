using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
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
    private readonly PlaywrightOfferMessenger? _automation;
    private readonly Dispatcher? _dispatcher;
    private bool _loading;

    private BoostingBotSettings Settings => _host.Settings;
    private OfferMessageSettings Config => Settings.Message;
    private PlaywrightMessageOptions Options => Config.Playwright;

    /// <summary>
    /// The combo shows a readable label per channel rather than the enum name; the seller
    /// has to be able to tell "browser dedicato" from "browser integrato" at a glance.
    /// </summary>
    public IReadOnlyList<DeliveryChoice> Deliveries { get; } =
    [
        new(MessageDelivery.PlaywrightBrowser, "Browser dedicato (consigliato)"),
        new(MessageDelivery.AutoBrowser, "Browser integrato (scheda Chat)"),
        new(MessageDelivery.ClipboardOnly, "Solo appunti (manuale)")
    ];

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
    [ObservableProperty] private bool _splitMessages;
    [ObservableProperty] private string _conversationUrl = "";
    [ObservableProperty] private int _delaySeconds;
    [ObservableProperty] private string _chatUrl = "";
    [ObservableProperty] private int _maxAttempts;
    [ObservableProperty] private string _chatScript = "";

    // ---- Dedicated automation browser (Playwright) ----
    [ObservableProperty] private bool _playwrightEnabled;
    [ObservableProperty] private bool _playwrightHeadless;
    [ObservableProperty] private string _playwrightProfilePath = "";
    [ObservableProperty] private string _composerSelector = "";
    [ObservableProperty] private string _sendButtonSelector = "";
    [ObservableProperty] private string _fileInputSelector = "";
    [ObservableProperty] private string _attachButtonSelector = "";

    [ObservableProperty] private string _automationStatus = "";
    [ObservableProperty] private bool _isOpeningAutomation;

    /// <summary>True when the dedicated browser is the channel the bot will actually use.</summary>
    public bool UsesAutomationBrowser => Delivery == MessageDelivery.PlaywrightBrowser;

    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _bannerStatus = "Nessun banner selezionato";
    [ObservableProperty] private string _splitStatus = "";
    [ObservableProperty] private int _messageCount;
    [ObservableProperty] private bool _hasBanner;

    public MessageViewModel(SettingsHost host, PlaywrightOfferMessenger? automation = null, Dispatcher? dispatcher = null)
    {
        _host = host;
        _automation = automation;
        _dispatcher = dispatcher;

        foreach (var (token, description) in OfferMessageComposer.Placeholders)
        {
            Placeholders.Add(new PlaceholderChip(token, description));
        }

        if (_automation is not null)
        {
            AutomationStatus = _automation.Status;

            // The messenger reports from whatever thread the browser answered on; the
            // binding has to be updated on the UI thread or WPF tears the string apart.
            _automation.StatusChanged += OnAutomationStatusChanged;
        }

        Reload();
    }

    private void OnAutomationStatusChanged()
    {
        var status = _automation?.Status ?? "";

        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            AutomationStatus = status;
            return;
        }

        _dispatcher.BeginInvoke(() => AutomationStatus = status);
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
            SplitMessages = Config.SplitMessages;
            ConversationUrl = Config.ConversationUrl;
            DelaySeconds = Config.DelaySeconds;
            ChatUrl = Config.ChatUrl;
            MaxAttempts = Config.MaxAttempts;
            ChatScript = Config.ChatScript;

            PlaywrightEnabled = Options.Enabled;
            PlaywrightHeadless = Options.Headless;
            PlaywrightProfilePath = Options.ProfilePath;
            ComposerSelector = Options.ComposerSelector;
            SendButtonSelector = Options.SendButtonSelector;
            FileInputSelector = Options.FileInputSelector;
            AttachButtonSelector = Options.AttachButtonSelector;
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
        Config.SplitMessages = SplitMessages;
        Config.ConversationUrl = (ConversationUrl ?? "").Trim();
        Config.DelaySeconds = Math.Max(0, DelaySeconds);
        Config.ChatUrl = string.IsNullOrWhiteSpace(ChatUrl) ? OfferMessageSettings.DefaultChatUrl : ChatUrl.Trim();
        Config.MaxAttempts = Math.Clamp(MaxAttempts, 1, 10);
        Config.ChatScript = ChatScript ?? "";

        Options.Enabled = PlaywrightEnabled;
        Options.Headless = PlaywrightHeadless;
        Options.ProfilePath = (PlaywrightProfilePath ?? "").Trim();
        Options.ComposerSelector = ComposerSelector ?? "";
        Options.SendButtonSelector = SendButtonSelector ?? "";
        Options.FileInputSelector = FileInputSelector ?? "";
        Options.AttachButtonSelector = AttachButtonSelector ?? "";
    }

    partial void OnEnabledChanged(bool value) => ApplyAndPreview();

    partial void OnDeliveryChanged(MessageDelivery value)
    {
        OnPropertyChanged(nameof(UsesAutomationBrowser));
        ApplyAndPreview();
    }

    partial void OnPlaywrightEnabledChanged(bool value) => ApplyAndPreview();
    partial void OnPlaywrightHeadlessChanged(bool value) => ApplyAndPreview();
    partial void OnPlaywrightProfilePathChanged(string value) => ApplyAndPreview();
    partial void OnComposerSelectorChanged(string value) => ApplyAndPreview();
    partial void OnSendButtonSelectorChanged(string value) => ApplyAndPreview();
    partial void OnFileInputSelectorChanged(string value) => ApplyAndPreview();
    partial void OnAttachButtonSelectorChanged(string value) => ApplyAndPreview();

    partial void OnTemplateChanged(string value) => ApplyAndPreview();
    partial void OnBannerPathChanged(string value) => ApplyAndPreview();
    partial void OnCopyToClipboardChanged(bool value) => ApplyAndPreview();
    partial void OnAttachBannerChanged(bool value) => ApplyAndPreview();
    partial void OnStrictBuyerMatchChanged(bool value) => ApplyAndPreview();
    partial void OnSplitMessagesChanged(bool value) => ApplyAndPreview();
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

    /// <summary>Marks where one chat message ends and the next begins, in the preview only.</summary>
    private const string Separator = "\n\n───  messaggio successivo  ───\n\n";

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

        var composed = OfferMessageComposer.Compose(Template ?? "", example, quote, Settings.DefaultDeliveryTime);
        var parts = OfferMessageComposer.Split(composed, SplitMessages);

        // Show the message boundaries, not just the text: the whole point of splitting is
        // that the buyer gets several chat bubbles, and the preview has to make that visible.
        MessageCount = parts.Count;
        SplitStatus = parts.Count switch
        {
            0 => "Nessun messaggio: il template e' vuoto",
            1 => "1 messaggio in chat",
            _ => $"{parts.Count} messaggi in chat, uno per blocco separato da una riga vuota"
        };

        Preview = string.Join(Separator, parts);
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

    /// <summary>
    /// Opens the dedicated automation browser so the seller signs in to Eldorado once, in
    /// the very profile the bot will reuse. Without this step the first delivery of the
    /// session would land on a logged-out page and be reported as "chat non pronta".
    /// </summary>
    [RelayCommand]
    private async Task OpenAutomationAsync()
    {
        if (_automation is null)
        {
            AutomationStatus = "Canale automatico non disponibile in questa finestra.";
            return;
        }

        Apply();
        _host.Save();

        IsOpeningAutomation = true;

        try
        {
            await _automation.OpenSessionAsync();
        }
        catch (Exception ex)
        {
            AutomationStatus = $"Browser non avviato: {ex.Message}";
        }
        finally
        {
            IsOpeningAutomation = false;
        }
    }

    /// <summary>Puts the selectors back to the broad defaults after an experiment went wrong.</summary>
    [RelayCommand]
    private void ResetSelectors()
    {
        var defaults = new PlaywrightMessageOptions();
        ComposerSelector = defaults.ComposerSelector;
        SendButtonSelector = defaults.SendButtonSelector;
        FileInputSelector = defaults.FileInputSelector;
        AttachButtonSelector = defaults.AttachButtonSelector;
    }

    [RelayCommand]
    private void Save()
    {
        Apply();
        _host.Save();

        // Save normalises clamped numbers and empty selectors; read them back so the boxes
        // show what the bot will really use rather than what was typed.
        Reload();
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

/// <summary>One entry of the delivery-channel combo: the enum value plus what to call it.</summary>
public sealed record DeliveryChoice(MessageDelivery Value, string Label);
