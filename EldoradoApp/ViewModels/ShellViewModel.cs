using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EldoradoApp.Models;
using EldoradoApp.Services;
using EldoradoApp.Services.Licensing;

namespace EldoradoApp.ViewModels;

/// <summary>
/// The whole application, in one window. It owns the shared settings, the sub-screens
/// and the bot itself; navigation only toggles which panel is visible, so the embedded
/// browser keeps its session and nothing ever opens a second window.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private const int MaxLogItems = 400;

    private readonly EldoradoBackend _backend;
    private readonly SettingsHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly AutoOfferEngine _engine;
    private readonly LicenseService _license;

    private CancellationTokenSource? _loopCts;
    private DispatcherTimer? _licenseTimer;

    public SettingsHost Host => _host;

    public RequestsFeedViewModel Feed { get; }
    public PricingViewModel Pricing { get; }
    public UnitsViewModel Units { get; }
    public ExtrasViewModel Extras { get; }
    public MessageViewModel Message { get; }
    public AccountViewModel Account { get; }
    public LicenseViewModel License { get; }

    /// <summary>The follow-up message pipeline; its automatic channel is wired by the view.</summary>
    public OfferMessageDispatcher Messages { get; }

    public ObservableCollection<NavItem> Sections { get; } =
    [
        new(AppSection.Dashboard, "Richieste", "ViewDashboard", "Feed live e prezzo calcolato"),
        new(AppSection.Pricing, "Prezzi", "CurrencyEur", "Prezzo base, listino per divisione e regioni"),
        new(AppSection.Units, "Placement & Wins", "Counter", "Prezzo per partita e per vittoria"),
        new(AppSection.Extras, "Extra", "PlusBoxMultiple", "DuoQ, streaming e altre opzioni"),
        new(AppSection.Message, "Messaggio", "MessageTextFast", "Testo automatico e banner"),
        new(AppSection.Chat, "Chat", "Web", "Browser integrato Eldorado"),
        new(AppSection.Account, "Account", "AccountCog", "Accesso, gioco e categorie"),
        new(AppSection.License, "Licenza", "KeyVariant", "Stato della chiave, scadenza e rinnovo"),
    ];

    public ObservableCollection<AutoOfferLogItem> Activity { get; } = [];

    [ObservableProperty] private AppSection _section = AppSection.Dashboard;
    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private string _sectionTitle = "Richieste";
    [ObservableProperty] private string _sectionHint = "Feed live e prezzo calcolato";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NotRunning))] private bool _isRunning;
    [ObservableProperty] private string _statusMessage = "Pronto";
    [ObservableProperty] private int _offersSent;
    [ObservableProperty] private int _offersWon;

    // ---- In-app login overlay (replaces the old pop-up window) ----
    [ObservableProperty] private bool _isLoginVisible;
    [ObservableProperty] private string _loginUrl = "";
    [ObservableProperty] private string _loginTitle = "Accedi con Google";

    /// <summary>True while the seller is signing in on the Eldorado website itself.</summary>
    [ObservableProperty] private bool _isSiteLogin;

    // ---- Embedded browser availability ----
    [ObservableProperty] private bool _isBrowserAvailable = WebViewEnvironment.IsInstalled;
    [ObservableProperty] private string _browserStatus = WebViewEnvironment.StatusText;

    public bool IsBrowserMissing => !IsBrowserAvailable;

    /// <summary>Re-reads the WebView2 status after an install or a failed attempt.</summary>
    public void RefreshBrowserStatus()
    {
        IsBrowserAvailable = WebViewEnvironment.IsInstalled;
        BrowserStatus = WebViewEnvironment.StatusText;
        OnPropertyChanged(nameof(IsBrowserMissing));
    }

    /// <summary>Message shown under the login overlay while we wait for the session.</summary>
    [ObservableProperty] private string _loginHint =
        "Premi «Login» sul sito e accedi come faresti su Chrome (Google oppure email e password). " +
        "Appena sei dentro, questa finestra si chiude da sola.";

    /// <summary>
    /// Ways to read Eldorado's session cookie, one per embedded browser. They share the
    /// same WebView2 profile, but registering every one of them means a browser that
    /// failed to start can't silently break sign-in.
    /// </summary>
    private readonly List<Func<Task<string?>>> _siteTokenReaders = [];

    public void RegisterSiteTokenReader(Func<Task<string?>> reader) => _siteTokenReaders.Add(reader);

    public bool HasSiteTokenReader => _siteTokenReaders.Count > 0;

    /// <summary>The first session token any of the browsers can see.</summary>
    public async Task<string?> ReadSiteTokenAsync()
    {
        foreach (var reader in _siteTokenReaders)
        {
            try
            {
                if (await reader() is { } token)
                {
                    return token;
                }
            }
            catch (Exception ex)
            {
                ApiLog.Write($"Site token reader failed: {ex.Message}");
            }
        }

        return null;
    }

    private CancellationTokenSource? _siteLoginCts;
    private DispatcherTimer? _tokenRefreshTimer;

    // ---- Google availability check ----
    [ObservableProperty] private string _googleAuthStatus =
        "Non ancora verificato in questa sessione (ultimo controllo noto: ATTIVO).";
    [ObservableProperty] private bool _isCheckingGoogle;

    /// <summary>
    /// Runs the Hosted-UI probe in the overlay browser. Supplied by the view because the
    /// check needs a real browser — Cloudflare challenges every headless client.
    /// </summary>
    public Func<string, CancellationToken, Task<GoogleAuthVerdict>>? GoogleProbe { get; set; }

    public bool NotRunning => !IsRunning;

    /// <summary>True while the bot would submit for real (used for the red warning strip).</summary>
    public bool IsArmed => IsRunning && !_host.Settings.DryRun;

    /// <summary>True while the licence is valid but close enough to its end to warn about.</summary>
    public bool IsLicenseExpiringSoon => _license.IsExpiringSoon;

    /// <summary>The renewal nag shown in the rail during the last few days.</summary>
    public string LicenseWarning => _license.Info is null
        ? ""
        : $"Licenza in scadenza fra {_license.DaysLeft} giorn{(_license.DaysLeft == 1 ? "o" : "i")} " +
          $"· rinnova su Discord {LicenseOptions.DiscordContact}";

    public ShellViewModel(EldoradoBackend backend, LicenseService license)
    {
        _backend = backend;
        _license = license;
        _dispatcher = Application.Current.Dispatcher;
        _host = new SettingsHost();

        Feed = new RequestsFeedViewModel(backend, _host);
        Pricing = new PricingViewModel(_host);
        Units = new UnitsViewModel(_host);
        Extras = new ExtrasViewModel(_host);
        Message = new MessageViewModel(_host);
        Account = new AccountViewModel(backend, _host);
        License = new LicenseViewModel(license);

        Messages = new OfferMessageDispatcher(() => _host.Settings, new ClipboardOfferMessenger(_dispatcher));
        Messages.Delivered += record => _dispatcher.Invoke(() => Message.Record(record));

        _engine = backend.CreateAutoOfferEngine(() => _host.Settings, Messages);
        _engine.Activity += OnEngineActivity;

        Account.SignedInChanged += () => _ = Feed.RefreshAsync();
        _host.Changed += () => OnPropertyChanged(nameof(IsArmed));

        _license.Changed += OnLicenseChanged;
        StartLicenseWatchdog();

        SelectedNav = Sections[0];
    }

    /// <summary>
    /// A licence that runs out mid-session has to stop the bot, not merely grey out a
    /// button — the app can sit open for days on end, so the expiry is re-checked on a
    /// timer rather than only at startup.
    /// </summary>
    private void StartLicenseWatchdog()
    {
        _licenseTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _licenseTimer.Tick += (_, _) =>
        {
            _license.Refresh();
            _ = _license.RefreshRevocationAsync();
        };
        _licenseTimer.Start();
    }

    private void OnLicenseChanged() => _dispatcher.Invoke(() =>
    {
        OnPropertyChanged(nameof(IsLicenseExpiringSoon));
        OnPropertyChanged(nameof(LicenseWarning));

        if (_license.IsValid)
        {
            return;
        }

        if (IsRunning)
        {
            StopBot();
        }

        StatusMessage = _license.Message;
        Navigate(AppSection.License);
    });

    /// <summary>Startup: restore the session, load games/categories and fill the feed.</summary>
    public async Task InitializeAsync()
    {
        StatusMessage = "Ripristino sessione…";
        var signedIn = await _backend.TrySignInFromStoreAsync();
        Account.SyncConnection();

        if (signedIn)
        {
            await Account.LoadGamesAsync();
        }

        await Feed.InitializeAsync();
        StatusMessage = signedIn ? "Sessione ripristinata" : "Accedi dalla scheda Account per iniziare";
    }

    [RelayCommand]
    private void Navigate(AppSection section)
    {
        var item = Sections.FirstOrDefault(s => s.Section == section);
        SelectedNav = item;   // the rail selection drives Section (see OnSelectedNavChanged)
        Section = section;
        SectionTitle = item?.Title ?? section.ToString();
        SectionHint = item?.Hint ?? "";
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null || value.Section == Section)
        {
            return;
        }

        Section = value.Section;
        SectionTitle = value.Title;
        SectionHint = value.Hint;
    }

    [RelayCommand]
    private void StartBot()
    {
        if (IsRunning)
        {
            return;
        }

        // Re-checked here and not just at startup: the licence can lapse while the app is
        // open, and this is the one command that spends the seller's money.
        if (_license.Refresh() != LicenseState.Valid)
        {
            StatusMessage = _license.Message;
            Navigate(AppSection.License);
            return;
        }

        if (!_backend.IsSignedIn)
        {
            StatusMessage = "Accedi prima di avviare il bot";
            Navigate(AppSection.Account);
            return;
        }

        _host.Settings.Enabled = true;
        _host.Save();

        _loopCts = new CancellationTokenSource();
        _ = _engine.RunLoopAsync(_loopCts.Token);
        IsRunning = true;
        OnPropertyChanged(nameof(IsArmed));
        StatusMessage = _host.Settings.DryRun ? "Bot avviato · DRY-RUN (simulazione)" : "Bot avviato · LIVE";
    }

    [RelayCommand]
    private void StopBot()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;

        _host.Settings.Enabled = false;
        _host.Save();

        IsRunning = false;
        OnPropertyChanged(nameof(IsArmed));
        StatusMessage = "Bot fermato";
    }

    [RelayCommand]
    private async Task RunOnceAsync()
    {
        if (_license.Refresh() != LicenseState.Valid)
        {
            StatusMessage = _license.Message;
            Navigate(AppSection.License);
            return;
        }

        if (!_backend.IsSignedIn)
        {
            StatusMessage = "Accedi prima di eseguire un ciclo";
            return;
        }

        StatusMessage = "Ciclo singolo in corso…";
        await _engine.RunOnceAsync();
        StatusMessage = "Ciclo singolo completato";
    }

    [RelayCommand]
    private void ClearActivity() => Activity.Clear();

    [RelayCommand]
    private void OpenApiLog()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ApiLog.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossibile aprire il log: {ex.Message}";
        }
    }

    /// <summary>Closes the overlay and abandons whatever sign-in was running in it.</summary>
    [RelayCommand]
    private void CancelLogin()
    {
        _siteLoginCts?.Cancel();
        IsLoginVisible = false;
    }

    // ---- Signing in through the embedded browser ----

    /// <summary>
    /// Opens Eldorado inside the app and waits for the seller to sign in — with Google,
    /// email or anything else the site offers — then borrows the session it creates.
    /// </summary>
    [RelayCommand]
    private Task SignInWithSiteAsync() =>
        SignInInBrowserAsync(EldoradoSiteSession.LoginUrl, "Accedi a Eldorado");

    /// <summary>
    /// Shows <paramref name="startUrl"/> in the overlay and polls the browser's cookie jar
    /// until Eldorado's session token appears — whatever route the seller took to get there.
    /// </summary>
    private async Task SignInInBrowserAsync(string startUrl, string title)
    {
        if (!WebViewEnvironment.IsInstalled)
        {
            RefreshBrowserStatus();
            StatusMessage = "WebView2 non installato: usa email e password, oppure incolla il token. " +
                            "Installa WebView2 per l'accesso dal sito.";
            Navigate(AppSection.Chat);
            return;
        }

        if (!HasSiteTokenReader)
        {
            StatusMessage = "Browser integrato non ancora pronto: apri la scheda Chat e riprova.";
            return;
        }

        // Already signed in inside the app's browser? Take that session straight away.
        var existing = await ReadSiteTokenAsync();
        if (existing is not null)
        {
            if (existing == _backend.CurrentIdToken && _backend.IsSignedIn)
            {
                StatusMessage = "Sei già connesso — sessione del browser integrato attiva.";
                return;
            }

            await AdoptTokenAsync(existing);
            return;
        }

        _siteLoginCts?.Dispose();
        _siteLoginCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        IsSiteLogin = true;
        LoginTitle = title;
        LoginUrl = startUrl;
        LoginHint = "Premi «Login» sul sito e accedi. Appena sei dentro, questa finestra si chiude da sola.";
        IsLoginVisible = true;
        StatusMessage = "Accedi nella finestra: appena sei dentro, il bot prende la sessione.";

        try
        {
            var token = await WaitForSiteTokenAsync(_siteLoginCts.Token);
            if (token is null)
            {
                StatusMessage = "Accesso annullato o non completato.";
                return;
            }

            await AdoptTokenAsync(token);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Accesso fallito: {ex.Message}";
        }
        finally
        {
            IsLoginVisible = false;
            IsSiteLogin = false;
        }
    }

    /// <summary>
    /// Manual escape hatch on the overlay: re-reads the cookie now and says plainly what
    /// it found, instead of leaving the seller staring at a window that never closes.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmLoginAsync()
    {
        var token = await ReadSiteTokenAsync();
        if (token is null)
        {
            LoginHint = "Non riesco ancora a vedere la sessione. Completa l'accesso sul sito " +
                        "(in alto a destra deve comparire il tuo avatar), poi riprova. " +
                        $"I dettagli finiscono in {ApiLog.FilePath}.";
            return;
        }

        _siteLoginCts?.Cancel();
        IsLoginVisible = false;
        IsSiteLogin = false;
        await AdoptTokenAsync(token);
    }

    /// <summary>Hands a freshly captured site session to the API client and wakes the app up.</summary>
    private async Task AdoptTokenAsync(string token)
    {
        _backend.UseManualToken(token);
        Account.SyncConnection();
        await Account.LoadGamesAsync();
        await Feed.RefreshAsync();

        StartTokenRefresh();
        StatusMessage = "Connesso ✓ — sessione presa dal browser integrato";
    }

    /// <summary>
    /// Polls until the browser holds a session the API client isn't already using.
    /// Anything valid counts — waiting for a token that <i>changed</i> would hang forever
    /// for a seller who was signed in on the site all along.
    /// </summary>
    private async Task<string?> WaitForSiteTokenAsync(CancellationToken cancellationToken)
    {
        var current = _backend.CurrentIdToken;
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromSeconds(1.5);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(step, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            var token = await ReadSiteTokenAsync();
            if (token is not null && (token != current || !_backend.IsSignedIn))
            {
                return token;
            }

            waited += step;
            if (waited > TimeSpan.FromSeconds(45) && token is null)
            {
                LoginHint = "Hai già fatto l'accesso? Se il sito ti mostra il tuo avatar ma questa " +
                            "finestra resta aperta, premi «Ho fatto l'accesso» qui sopra.";
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps the borrowed session alive: the website silently renews its cookie, so
    /// re-reading it every few minutes means the bot never dies on a 30-minute expiry.
    /// </summary>
    private void StartTokenRefresh()
    {
        if (_tokenRefreshTimer is not null)
        {
            return;
        }

        _tokenRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(4) };
        _tokenRefreshTimer.Tick += async (_, _) => await RefreshSiteTokenAsync();
        _tokenRefreshTimer.Start();
    }

    /// <summary>Re-reads the browser session cookie and hands it to the API client.</summary>
    public async Task RefreshSiteTokenAsync()
    {
        if (!HasSiteTokenReader)
        {
            return;
        }

        try
        {
            var token = await ReadSiteTokenAsync();
            if (token is not null && token != _backend.CurrentIdToken)
            {
                _backend.UseManualToken(token);
                Account.SyncConnection();
            }
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Token refresh failed: {ex.Message}");
        }
    }

    /// <summary>Picks up an existing browser session at startup, if the seller left one.</summary>
    public async Task TryAdoptBrowserSessionAsync()
    {
        if (_backend.IsSignedIn || !HasSiteTokenReader)
        {
            return;
        }

        var token = await ReadSiteTokenAsync();
        if (token is null)
        {
            return;
        }

        try
        {
            _backend.UseManualToken(token);
            Account.SyncConnection();
            await Account.LoadGamesAsync();
            await Feed.RefreshAsync();
            StartTokenRefresh();
            StatusMessage = "Sessione ripresa dal browser integrato";
        }
        catch (Exception ex)
        {
            ApiLog.Write($"Adopt browser session failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks whether Eldorado has switched Google sign-in on for the bot client, using
    /// the app's own browser (the only client Cloudflare lets through).
    /// </summary>
    [RelayCommand]
    private async Task CheckGoogleAuthAsync()
    {
        if (GoogleProbe is null || IsCheckingGoogle)
        {
            return;
        }

        if (!WebViewEnvironment.IsInstalled)
        {
            RefreshBrowserStatus();
            GoogleAuthStatus = "Verifica non possibile: serve il runtime WebView2 (Cloudflare blocca ogni " +
                               "controllo che non giri in un browser vero).";
            return;
        }

        IsCheckingGoogle = true;
        GoogleAuthStatus = "Verifica in corso… (la pagina si apre qui dentro)";

        try
        {
            var (authorizeUrl, _) = _backend.OAuth.BeginSignIn();

            LoginTitle = "Verifica accesso Google · Eldorado";
            LoginUrl = authorizeUrl;
            IsLoginVisible = true;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var verdict = await GoogleProbe(authorizeUrl, timeout.Token);

            GoogleAuthStatus = verdict.Message;
            StatusMessage = $"Accesso Google · {verdict.State}";
            ApiLog.Write($"Google auth probe: {verdict.State} — {verdict.Message} ({verdict.FinalUrl})");
        }
        catch (Exception ex)
        {
            GoogleAuthStatus = $"Verifica fallita: {ex.Message}";
        }
        finally
        {
            IsLoginVisible = false;
            IsCheckingGoogle = false;
        }
    }

    private void OnEngineActivity(AutoOfferEvent e)
    {
        _dispatcher.Invoke(() =>
        {
            Activity.Insert(0, new AutoOfferLogItem(e));
            while (Activity.Count > MaxLogItems)
            {
                Activity.RemoveAt(Activity.Count - 1);
            }

            OffersSent = _engine.OfferedCount;
            OffersWon = _engine.WonCount;

            if (e.Outcome is AutoOfferOutcome.Submitted or AutoOfferOutcome.Accepted or AutoOfferOutcome.Error)
            {
                StatusMessage = e.Message;
            }
        });
    }
}
