using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDb.Models;
using SteamDb.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SteamDb.ViewModels;

/// <summary>
/// The main window. Split by concern into partial files:
/// <list type="bullet">
/// <item>this file — dependencies, credentials, store rows, what can run (CanExecute + hints), links;</item>
/// <item><c>.Settings</c> — remembered settings, Save / Import, store ticks;</item>
/// <item><c>.Exports</c> — CSV / Notion / Sheets / library / duplicates / dropped files;</item>
/// <item><c>.Background</c> — auto-sync, tray "Sync now", close to tray, start at login;</item>
/// <item><c>.Status</c> — result banner, last sync / progress lines, sync history, app updates.</item>
/// </list>
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private string? googleSheetsTableName;

    [ObservableProperty] private string? steamApiKey;

    [ObservableProperty] private string? steamId;

    [ObservableProperty] private string? notionToken;

    [ObservableProperty] private string? dbId;

    /// <summary>Per-store connect state + commands. Epic uses the system-browser paste flow; GOG and
    /// Xbox log in through the embedded WebView.</summary>
    public StoreConnectionViewModel Epic { get; }

    public StoreConnectionViewModel Gog { get; }

    public StoreConnectionViewModel Xbox { get; }

    /// <summary>Busy / progress-bar state + cancellation for the running export.</summary>
    public ProgressViewModel Progress { get; } = new();

    private readonly ISecretStore _secrets;
    private readonly IWebAuthenticator _webAuth;
    private readonly IStoreClientFactory _clients;
    private readonly IGameLibraryService _library;
    private readonly NotionGameExporter _notionExporter;
    private readonly GoogleSheetsGameExporter _sheetsExporter;
    private readonly IDialogService _dialogs;
    private readonly CsvFileService _csv;
    private readonly ILogService _log;
    private readonly IUpdateService _updates;
    private readonly StoreSelection _selection;
    private readonly IgnoredGames _ignored;
    private readonly IAutoStart _autoStart;
    private readonly BacklogProgress _backlog;
    private readonly SyncLog _syncLog;
    private readonly SyncHistory _syncHistory;
    private readonly FirstSeenGames _firstSeen;

    public MainWindowViewModel(
        ISecretStore secrets,
        IWebAuthenticator webAuth,
        IStoreClientFactory clients,
        IGameLibraryService library,
        NotionGameExporter notionExporter,
        GoogleSheetsGameExporter sheetsExporter,
        IDialogService dialogs,
        CsvFileService csv,
        ILogService log,
        IUpdateService updates,
        StoreSelection selection,
        IgnoredGames ignored,
        IAutoStart autoStart,
        BacklogProgress backlog,
        SyncLog syncLog,
        FirstSeenGames firstSeen)
    {
        _firstSeen = firstSeen;
        _secrets = secrets;
        _webAuth = webAuth;
        _clients = clients;
        _library = library;
        _notionExporter = notionExporter;
        _sheetsExporter = sheetsExporter;
        _dialogs = dialogs;
        _csv = csv;
        _log = log;
        _updates = updates;
        _selection = selection;
        _ignored = ignored;
        _autoStart = autoStart;
        _backlog = backlog;
        _syncLog = syncLog;
        _syncHistory = new SyncHistory(secrets);

        LoadPersistedSettings();
        RefreshLastSync();
        LoadStartAtLogin();

        // Epic stays on the system-browser paste flow — its login blocks embedded webviews (CAPTCHA);
        // GOG/Xbox log in through the embedded WebView.
        Epic = new StoreConnectionViewModel(
            "Epic",
            () => _clients.CreateEpic(),
            EpicAuthCodeParser.Extract,
            () => _dialogs.Clipboard,
            _dialogs.ShowErrorAsync,
            _log,
            supportsCodeInput: true,
            codePlaceholder: "Epic authorization code",
            openInfo: () => _dialogs.OpenLink(ReadmeLink("epic-games")));

        Gog = new StoreConnectionViewModel(
            "GOG",
            () => _clients.CreateGog(),
            GogAuthCodeParser.Extract,
            () => _dialogs.Clipboard,
            _dialogs.ShowErrorAsync,
            _log,
            _webAuth,
            connectingStatus: "Opening GOG login…",
            beginBusy: Progress.Begin,
            endBusy: Progress.End,
            openInfo: () => _dialogs.OpenLink(ReadmeLink("gog")));

        Xbox = new StoreConnectionViewModel(
            "Xbox",
            () => _clients.CreateXbox(),
            XboxAuthCodeParser.Extract,
            () => _dialogs.Clipboard,
            _dialogs.ShowErrorAsync,
            _log,
            _webAuth,
            connectingStatus: "Opening Xbox login…",
            beginBusy: Progress.Begin,
            endBusy: Progress.End,
            onConnected: OnXboxConnectedAsync,
            openInfo: () => _dialogs.OpenLink(ReadmeLink("xbox")));

        // A store (dis)connecting changes whether there's anything to export.
        foreach (var store in new[] { Epic, Gog, Xbox })
            store.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StoreConnectionViewModel.IsConnected)) RefreshCanExport();
            };
        RefreshCanExport();

        // The IDE previewer builds this view model too: no sessions, update checks or auto-sync there.
        if (Design.IsDesignMode) return;

        _ = Epic.InitializeFromCacheAsync();
        _ = Gog.InitializeFromCacheAsync();
        _ = Xbox.InitializeFromCacheAsync();
        _ = CheckForUpdateAsync();
        ShowWhatsNew();
        StartAutoSyncTimer();
    }

    // ---- design time -------------------------------------------------------------------------

    /// <summary>
    /// Design-time only (used by the Avalonia previewer's <c>Design.DataContext</c>). At runtime the
    /// app resolves the parameterised constructor above through the DI container (see App.axaml.cs).
    /// </summary>
    public MainWindowViewModel()
        : this(BuildDesignTimeDependencies())
    {
    }

    private MainWindowViewModel(Dependencies d)
        : this(d.Secrets, d.WebAuth, d.Clients, d.Library, d.NotionExporter, d.SheetsExporter, d.Dialogs,
            d.Csv, d.Log, d.Updates, d.Selection, d.Ignored, d.AutoStart, d.Backlog, d.SyncLog, d.FirstSeen)
    {
    }

    // The production graph, but in-memory / no-op where it would touch the user's settings, tokens or
    // log: the IDE runs several previewer hosts.
    private static Dependencies BuildDesignTimeDependencies()
    {
        ILogService log = new DesignTimeLog();
        ISecretStore secrets = new DesignTimeSecretStore();
        var clients = new StoreClientFactory(secrets, log);
        var selection = new StoreSelection();
        var ignored = new IgnoredGames(secrets);
        var backlog = new BacklogProgress(secrets);
        var firstSeen = new FirstSeenGames(secrets);
        var library = new GameLibraryService(clients, selection, ignored, firstSeen);
        return new Dependencies(
            secrets,
            new EmbeddedWebViewAuthenticator(DefaultTopLevel),
            clients,
            library,
            new NotionGameExporter(library, new NotionGateway(log), log, backlog),
            new GoogleSheetsGameExporter(secrets, library, log, backlog),
            new WindowDialogService(),
            new CsvFileService(log),
            log,
            new VelopackUpdateService(log),
            selection,
            ignored,
            new AutoStart(log),
            backlog,
            new SyncLog(secrets),
            firstSeen);
    }

    private sealed record Dependencies(
        ISecretStore Secrets,
        IWebAuthenticator WebAuth,
        IStoreClientFactory Clients,
        IGameLibraryService Library,
        NotionGameExporter NotionExporter,
        GoogleSheetsGameExporter SheetsExporter,
        IDialogService Dialogs,
        CsvFileService Csv,
        ILogService Log,
        IUpdateService Updates,
        StoreSelection Selection,
        IgnoredGames Ignored,
        IAutoStart AutoStart,
        BacklogProgress Backlog,
        SyncLog SyncLog,
        FirstSeenGames FirstSeen);

    private static TopLevel? DefaultTopLevel() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    // ---- what's needed before an export can run (drives CanExecute + the disabled-button tooltips) ----

    private bool HasSteamCredentials => !string.IsNullOrWhiteSpace(SteamApiKey) && !string.IsNullOrWhiteSpace(SteamId);

    // Epic/Gog/Xbox are still null while the constructor loads the settings.
    private bool HasSource =>
        (IncludeSteam && HasSteamCredentials) ||
        (IncludeEpic && Epic?.IsConnected == true) ||
        (IncludeGog && Gog?.IsConnected == true) ||
        (IncludeXbox && Xbox?.IsConnected == true);

    private const string NoSourceHint =
        "Enter your Steam API key and Steam ID, or connect Epic / GOG / Xbox (and keep the store ticked).";

    public bool CanFetchLibrary => HasSource;

    public bool CanExportNotion =>
        HasSource && !string.IsNullOrWhiteSpace(NotionToken) && !string.IsNullOrWhiteSpace(DbId);

    public bool CanExportSheets => HasSource && !string.IsNullOrWhiteSpace(GoogleSheetsTableName);

    /// <summary>Finding duplicates reads only the Notion database, so no store is needed.</summary>
    public bool CanFindNotionDuplicates => !string.IsNullOrWhiteSpace(NotionToken) && !string.IsNullOrWhiteSpace(DbId);

    /// <summary>Why the CSV / library buttons are disabled, or null.</summary>
    public string? LibraryHint => HasSource ? null : NoSourceHint;

    public string? NotionHint =>
        !HasSource ? NoSourceHint
        : CanExportNotion ? null
        : "Enter the Notion integration token and database ID.";

    public string? SheetsHint =>
        !HasSource ? NoSourceHint
        : CanExportSheets ? null
        : "Enter the Google Sheets table name.";

    partial void OnSteamApiKeyChanged(string? value) => RefreshCanExport();

    partial void OnSteamIdChanged(string? value) => RefreshCanExport();

    partial void OnNotionTokenChanged(string? value) => RefreshCanExport();

    partial void OnDbIdChanged(string? value) => RefreshCanExport();

    partial void OnGoogleSheetsTableNameChanged(string? value) => RefreshCanExport();

    private void RefreshCanExport()
    {
        OnPropertyChanged(nameof(CanFetchLibrary));
        OnPropertyChanged(nameof(CanExportNotion));
        OnPropertyChanged(nameof(CanExportSheets));
        OnPropertyChanged(nameof(LibraryHint));
        OnPropertyChanged(nameof(NotionHint));
        OnPropertyChanged(nameof(SheetsHint));
        ExportToCsvCommand.NotifyCanExecuteChanged();
        UpdateCsvCommand.NotifyCanExecuteChanged();
        ViewLibraryCommand.NotifyCanExecuteChanged();
        ExportToNotionCommand.NotifyCanExecuteChanged();
        ExportToGoogleSheetsCommand.NotifyCanExecuteChanged();
        FindNotionDuplicatesCommand.NotifyCanExecuteChanged();
    }

    // ---- Xbox post-connect check ---------------------------------------------------------------

    // After a successful interactive Xbox connect, pull the library once (live check + logs counts).
    private async Task OnXboxConnectedAsync(StoreConnectionViewModel _)
    {
        Progress.SetIndeterminate("Loading Xbox library…");
        await LogXboxLibrarySummaryAsync();
    }

    // Fetches the Xbox library once and logs the count — a live check that both the auth chain and
    // the data calls work.
    private async Task LogXboxLibrarySummaryAsync()
    {
        try
        {
            var client = _clients.CreateXbox();
            if (await client.TryAuthenticateFromCacheAsync() != StoreAuthFromCacheStatus.Authenticated)
                return;

            var games = await client.GetGamesAsync();
            _log.WriteInfo(
                $"Xbox: connect verified — {games.Count} played titles " +
                $"({games.Count(g => g.IsGamePass)} flagged Game Pass).");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Xbox library fetch (post-connect) failed");
        }
    }

    // ---- help links ----------------------------------------------------------------------------

    private static string ReadmeLink(string section) =>
        $"https://github.com/AleksandrPidlozhevich/SteamDb#{section}";

    [RelayCommand]
    private void OpenSteamApiKeyInfo() => _dialogs.OpenLink("https://steamcommunity.com/dev/apikey");

    [RelayCommand]
    private void OpenInfoLinkSteamId() => _dialogs.OpenLink(ReadmeLink("steam"));

    [RelayCommand]
    private void OpenInfoLinkNotionToken() => _dialogs.OpenLink("https://www.notion.so/profile/integrations");

    [RelayCommand]
    private void OpenInfoLinkNotionDbId() =>
        _dialogs.OpenLink("https://developers.notion.com/reference/retrieve-database");

    [RelayCommand]
    private void OpenInfoGoogleSheets() => _dialogs.OpenLink(ReadmeLink("google-sheets-beta"));

    [RelayCommand]
    private void OpenLinkKofi() => _dialogs.OpenLink("https://ko-fi.com/aliaksandrpidlazhevich");

    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(FileLogService.LogDirectory);
        _dialogs.OpenLink(FileLogService.LogDirectory);
    }
}
