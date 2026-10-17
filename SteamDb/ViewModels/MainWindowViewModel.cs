using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDb.Models;
using SteamDb.Services;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SteamDb.ViewModels;

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

    /// <summary>The running version, shown in the window corner (e.g. <c>v1.1.0</c>).</summary>
    public string AppVersion => $"v{_updates.CurrentVersion}";

    /// <summary>A newer version found on GitHub Releases at startup, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    private string? availableUpdateVersion;

    public bool IsUpdateAvailable => AvailableUpdateVersion is not null;

    /// <summary>Outcome of the last export ("✓ Notion: 12 added, 3 updated"), shown as a banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? resultMessage;

    /// <summary>Label of the banner's follow-up action ("Open file", "Open database"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultAction))]
    private string? resultActionLabel;

    private Action? _resultAction;

    /// <summary>A second banner action (e.g. "Undo" after Update .csv), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultSecondaryAction))]
    private string? resultSecondaryLabel;

    private Func<Task>? _resultSecondaryAction;

    public bool HasResultSecondaryAction => ResultSecondaryLabel is not null;

    public bool HasResult => ResultMessage is not null;

    /// <summary>"Last sync: today 14:20" under each export button; null when never synced.</summary>
    [ObservableProperty] private string? csvLastSync;

    [ObservableProperty] private string? notionLastSync;

    [ObservableProperty] private string? sheetsLastSync;

    /// <summary>"Completed 120 of 640 (19%)" from the last Notion export; null before one.</summary>
    [ObservableProperty] private string? notionProgress;

    [ObservableProperty] private string? sheetsProgress;

    private readonly SyncHistory _syncHistory;
    private readonly StoreSelection _selection;

    /// <summary>"Include in export" ticks per store (saved; also honoured by <c>--sync</c>).</summary>
    [ObservableProperty] private bool includeSteam = true;

    [ObservableProperty] private bool includeEpic = true;

    [ObservableProperty] private bool includeGog = true;

    [ObservableProperty] private bool includeXbox = true;

    public bool HasResultAction => ResultActionLabel is not null;

    /// <summary>In-app auto-sync (while the window is open): interval picker + targets (saved).</summary>
    public IReadOnlyList<string> AutoSyncIntervalLabels => AutoSyncSettings.IntervalLabels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoSyncOn))]
    private int autoSyncIntervalIndex;

    [ObservableProperty] private bool autoSyncNotion;

    [ObservableProperty] private bool autoSyncSheets;

    [ObservableProperty] private bool autoSyncCsv;

    /// <summary>The CSV last written by Export / Update .csv (saved): what CSV auto-sync updates.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoSyncCsvHint))]
    private string? csvFilePath;

    public string AutoSyncCsvHint => CsvFilePath is { } path
        ? $"Update {path} on the schedule (a .bak of the previous version is kept)"
        : "Export or update a CSV once and it's remembered here for auto-sync";

    public bool IsAutoSyncOn => AutoSyncIntervalIndex > 0;

    /// <summary>Closing the window keeps SteamDb running in the tray (saved).</summary>
    [ObservableProperty] private bool closeToTray;

    /// <summary>False on a Linux desktop without a tray: closing then really closes (set by <c>App</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseToTrayHint), nameof(StartAtLoginHint))]
    private bool isTrayAvailable = true;

    /// <summary>Close to tray only works when there's a tray to come back from.</summary>
    public bool KeepsRunningOnClose => CloseToTray && IsTrayAvailable;

    public string CloseToTrayHint => IsTrayAvailable
        ? "Closing the window keeps SteamDb running in the tray (so auto-sync keeps going). Quit from the tray icon's menu."
        : "This desktop has no system tray (on GNOME, install the AppIndicator extension), so closing the window quits SteamDb.";

    public string StartAtLoginHint => IsTrayAvailable
        ? "Start SteamDb hidden in the tray when you log in (turns on Close to tray as well)."
        : "Start SteamDb when you log in (the window opens, since this desktop has no tray).";

    /// <summary>Start hidden in the tray at login (a system login item, not a saved setting).</summary>
    [ObservableProperty] private bool startAtLogin;

    public bool CanStartAtLogin => _autoStart.IsSupported;

    private bool _syncingStartAtLogin;

    /// <summary>Raised when the window should be brought up (e.g. Sync now has nothing to do).</summary>
    public event Action? ShowWindowRequested;

    private readonly DispatcherTimer _autoSyncTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<SyncTarget, DateTimeOffset> _autoSyncFailures = [];


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
    private readonly IgnoredGames _ignored;
    private readonly IAutoStart _autoStart;
    private readonly BacklogProgress _backlog;
    private readonly SyncLog _syncLog;

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
        SyncLog syncLog)
    {
        _backlog = backlog;
        _syncLog = syncLog;
        _selection = selection;
        _ignored = ignored;
        _autoStart = autoStart;
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
        _syncHistory = new SyncHistory(secrets);

        LoadPersistedSettings();
        RefreshLastSync();

        _syncingStartAtLogin = true;
        StartAtLogin = _autoStart.IsSupported && _autoStart.IsEnabled;
        _syncingStartAtLogin = false;

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
            openInfo: () => _dialogs.OpenLink("https://github.com/AleksandrPidlozhevich/SteamDb#epic-games"));

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
            openInfo: () => _dialogs.OpenLink("https://github.com/AleksandrPidlozhevich/SteamDb#gog"));

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
            openInfo: () => _dialogs.OpenLink("https://github.com/AleksandrPidlozhevich/SteamDb#xbox"));

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

        // Checked once a minute, so the first auto-sync starts a minute after launch (after the
        // cached store sessions have been restored).
        _autoSyncTimer.Tick += async (_, _) => await RunDueAutoSyncAsync();
        _autoSyncTimer.Start();
    }

    private AutoSyncSettings CurrentAutoSync()
    {
        var targets = new List<SyncTarget>();
        if (AutoSyncNotion) targets.Add(SyncTarget.Notion);
        if (AutoSyncSheets) targets.Add(SyncTarget.Sheets);
        if (AutoSyncCsv) targets.Add(SyncTarget.Csv);
        var index = Math.Clamp(AutoSyncIntervalIndex, 0, AutoSyncSettings.Intervals.Count - 1);
        return new AutoSyncSettings(AutoSyncSettings.Intervals[index], targets);
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (_syncingStartAtLogin) return;
        try
        {
            _autoStart.SetEnabled(value);
            // Starting hidden only makes sense if closing keeps it in the tray too.
            if (value && IsTrayAvailable) CloseToTray = true;
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Couldn't change the login item");
            ShowResult($"⚠ Couldn't change \"Start at login\": {ex.Message}");
            _syncingStartAtLogin = true;
            StartAtLogin = !value;
            _syncingStartAtLogin = false;
        }
    }

    private void ApplyAutoSync(string? value)
    {
        var settings = AutoSyncSettings.Parse(value);
        var index = AutoSyncSettings.Intervals.ToList().IndexOf(settings.IntervalHours);
        AutoSyncIntervalIndex = Math.Max(index, 0);
        AutoSyncNotion = settings.Targets.Contains(SyncTarget.Notion);
        AutoSyncSheets = settings.Targets.Contains(SyncTarget.Sheets);
        AutoSyncCsv = settings.Targets.Contains(SyncTarget.Csv);
    }

    /// <summary>
    /// Runs the auto-sync targets that are due, like <c>--sync</c> does (Notion is applied without the
    /// preview), and reports the outcome in the result banner instead of a dialog. Skipped while
    /// anything else runs.
    /// </summary>
    private async Task RunDueAutoSyncAsync()
    {
        if (Progress.IsBusy) return;

        var due = AutoSyncPlanner.Due(
            CurrentAutoSync(),
            t => _syncHistory.Get(SyncHistory.KeyOf(t)),
            _autoSyncFailures,
            CanAutoSync,
            DateTimeOffset.Now);
        if (due.Count == 0) return;

        await RunSyncTargetsAsync(due, "Auto-sync");
    }

    private bool CanAutoSync(SyncTarget target) => target switch
    {
        SyncTarget.Notion => CanExportNotion,
        SyncTarget.Sheets => CanExportSheets,
        _ => CanFetchLibrary && CsvFilePath != null
    };

    /// <summary>
    /// Tray "Sync now": runs the targets ticked under Auto-sync right away (ignoring the schedule).
    /// With none ticked or ready, brings the window up with a hint instead.
    /// </summary>
    public async Task SyncNowAsync()
    {
        if (Progress.IsBusy) return;

        var targets = new List<SyncTarget>();
        if (AutoSyncNotion && CanExportNotion) targets.Add(SyncTarget.Notion);
        if (AutoSyncSheets && CanExportSheets) targets.Add(SyncTarget.Sheets);
        if (AutoSyncCsv && CanAutoSync(SyncTarget.Csv)) targets.Add(SyncTarget.Csv);
        if (targets.Count == 0)
        {
            ShowResult("Pick an Auto-sync interval and tick Notion / Sheets / CSV (with their fields filled in) to use Sync now.");
            ShowWindowRequested?.Invoke();
            return;
        }

        await RunSyncTargetsAsync(targets, "Sync");
    }

    private async Task RunSyncTargetsAsync(IReadOnlyList<SyncTarget> due, string label)
    {
        Progress.Begin($"{label}…");
        PersistSettings(); // HeadlessSync reads the remembered settings
        var started = DateTimeOffset.Now;
        var outcomes = new List<string>();
        var failed = false;
        try
        {
            foreach (var target in due)
            {
                var name = SyncHistory.KeyOf(target);
                string? lastLine = null;
                var output = new CallbackTextWriter(line =>
                {
                    lastLine = line;
                    Dispatcher.UIThread.Post(() => Progress.Status = $"{label} {name}: {line}");
                });
                var sync = new HeadlessSync(
                    _secrets, _library, _notionExporter, _sheetsExporter, _log, output, _selection, _syncLog, label);
                var code = await sync.RunAsync(new SyncOptions([target], null, null, false), Progress.Token);

                if (Progress.Token.IsCancellationRequested)
                {
                    // Don't restart right away: a cancel waits like a failure (retry in an hour).
                    foreach (var t in due) _autoSyncFailures[t] = started;
                    outcomes.Add("cancelled");
                    failed = true;
                    break;
                }

                if (code == 0)
                {
                    _autoSyncFailures.Remove(target);
                    outcomes.Add($"{name}: {lastLine}");
                }
                else
                {
                    _autoSyncFailures[target] = started;
                    outcomes.Add($"{name}: {lastLine ?? "failed"}");
                    failed = true;
                }
            }
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, $"{label} failed");
            foreach (var t in due) _autoSyncFailures[t] = started;
            outcomes.Add(ex.Message);
            failed = true;
        }
        finally
        {
            Progress.End();
            RefreshLastSync();
        }

        var message = $"{(failed ? "⚠" : "✓")} {label} {started:HH:mm} — {string.Join(" · ", outcomes)}";
        if (failed) ShowResult(message, "Open logs", OpenLogsFolder);
        else ShowResult(message);
    }

    private async Task CheckForUpdateAsync()
    {
        AvailableUpdateVersion = await _updates.CheckForUpdateAsync();
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (Progress.IsBusy) return;
        Progress.Begin($"Downloading update {AvailableUpdateVersion}…");
        try
        {
            // Constructed on the UI thread, so callbacks marshal back to it automatically.
            var reporter = new Progress<int>(percent =>
            {
                Progress.IsIndeterminate = false;
                Progress.Maximum = 100;
                Progress.Value = percent;
                Progress.Status = $"Downloading update {AvailableUpdateVersion}… {percent}%";
            });
            // Restarts the app into the new version on success.
            await _updates.DownloadAndRestartAsync(reporter, Progress.Token);
        }
        catch (OperationCanceledException)
        {
            _log.WriteInfo("Update download cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Error installing update");
            await _dialogs.ShowErrorAsync("Update error", ex);
        }
        finally
        {
            Progress.End();
        }
    }

    // After a successful interactive Xbox connect, pull the library once (live check + logs counts).
    private async Task OnXboxConnectedAsync(StoreConnectionViewModel _)
    {
        Progress.SetIndeterminate("Loading Xbox library…");
        await LogXboxLibrarySummaryAsync();
    }

    /// <summary>
    /// Design-time only (used by the Avalonia previewer's <c>Design.DataContext</c>). At runtime the
    /// app resolves the parameterised constructor above through the DI container (see App.axaml.cs).
    /// </summary>
    public MainWindowViewModel()
        : this(BuildDefaultDependencies())
    {
    }

    // Composes the default production dependency graph for the design-time constructor, so the
    // previewer gets a working view model without a DI container.
    private static Dependencies BuildDefaultDependencies()
    {
        // In-memory / no-op: the previewer must never touch the user's settings, tokens or log.
        ILogService log = new DesignTimeLog();
        ISecretStore secrets = new DesignTimeSecretStore();
        var clients = new StoreClientFactory(secrets, log);
        var selection = new StoreSelection();
        var ignored = new IgnoredGames(secrets);
        var backlog = new BacklogProgress(secrets);
        var library = new GameLibraryService(clients, selection, ignored);
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
            new SyncLog(secrets));
    }

    private MainWindowViewModel(Dependencies d)
        : this(d.Secrets, d.WebAuth, d.Clients, d.Library, d.NotionExporter, d.SheetsExporter, d.Dialogs,
            d.Csv, d.Log, d.Updates, d.Selection, d.Ignored, d.AutoStart, d.Backlog, d.SyncLog)
    {
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
        SyncLog SyncLog);

    private static TopLevel? DefaultTopLevel() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    [RelayCommand]
    private async Task SaveSettings()
    {
        var file = await _dialogs.PickSaveCsvAsync("steamdb-settings.csv");
        if (file == null) return;

        var content = AppSettingsService.Serialize(CurrentSettings());
        await _csv.WriteAsync(file, content);
        PersistSettings();
    }

    [RelayCommand]
    private async Task ImportSettings()
    {
        var file = await _dialogs.PickOpenCsvAsync("Import CSV File");
        if (file == null) return;

        var content = await _csv.ReadAsync(file);
        if (content == null) return;

        ApplyImportedSettings(content);
    }

    // Only overwrites the fields actually present in the settings file, then remembers them.
    private void ApplyImportedSettings(string content)
    {
        var settings = AppSettingsService.Parse(content);
        SteamApiKey = settings.SteamApiKey ?? SteamApiKey;
        SteamId = settings.SteamId ?? SteamId;
        NotionToken = settings.NotionToken ?? NotionToken;
        DbId = settings.DbId ?? DbId;
        GoogleSheetsTableName = settings.GoogleSheetsTableName ?? GoogleSheetsTableName;
        if (settings.ExcludedStores != null) ApplyExcludedStores(settings.ExcludedStores);
        if (settings.AutoSync != null) ApplyAutoSync(settings.AutoSync);
        if (settings.CloseToTray != null) CloseToTray = settings.CloseToTray.Value;
        if (settings.CsvFile != null) CsvFilePath = settings.CsvFile;
        PersistSettings();
    }

    /// <summary>Loads credentials remembered (encrypted) from a previous session.</summary>
    private void LoadPersistedSettings()
    {
        var content = _secrets.Load(AppSettingsService.SecretKey);
        if (string.IsNullOrEmpty(content)) return;

        var settings = AppSettingsService.Parse(content);
        SteamApiKey = settings.SteamApiKey ?? SteamApiKey;
        SteamId = settings.SteamId ?? SteamId;
        NotionToken = settings.NotionToken ?? NotionToken;
        DbId = settings.DbId ?? DbId;
        GoogleSheetsTableName = settings.GoogleSheetsTableName ?? GoogleSheetsTableName;
        ApplyExcludedStores(settings.ExcludedStores);
        ApplyAutoSync(settings.AutoSync);
        CloseToTray = settings.CloseToTray ?? false;
        CsvFilePath = settings.CsvFile;
    }

    private AppSettings CurrentSettings() =>
        new(SteamApiKey, SteamId, NotionToken, DbId, GoogleSheetsTableName, _selection.Serialize(),
            CurrentAutoSync().Serialize(), CloseToTray, CsvFilePath);

    private void ApplyExcludedStores(string? excluded)
    {
        _selection.Load(excluded);
        IncludeSteam = _selection.Includes(Platforms.Steam);
        IncludeEpic = _selection.Includes(Platforms.Epic);
        IncludeGog = _selection.Includes(Platforms.Gog);
        IncludeXbox = _selection.Includes(Platforms.Xbox);
    }

    partial void OnIncludeSteamChanged(bool value) => OnIncludeChanged(Platforms.Steam, value);

    partial void OnIncludeEpicChanged(bool value) => OnIncludeChanged(Platforms.Epic, value);

    partial void OnIncludeGogChanged(bool value) => OnIncludeChanged(Platforms.Gog, value);

    partial void OnIncludeXboxChanged(bool value) => OnIncludeChanged(Platforms.Xbox, value);

    private void OnIncludeChanged(string store, bool included)
    {
        _selection.SetIncluded(store, included);
        RefreshCanExport();
    }

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

    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(FileLogService.LogDirectory);
        _dialogs.OpenLink(FileLogService.LogDirectory);
    }

    /// <summary>Called on shutdown, so values typed into the fields survive a restart.</summary>
    public void SaveOnExit() => PersistSettings();

    /// <summary>Persists the current credentials to the encrypted secret store (also read by the
    /// command-line <c>--sync</c>). Runs on Save/Import, when an export starts, and on exit.</summary>
    private void PersistSettings()
    {
        _secrets.Save(AppSettingsService.SecretKey,
            AppSettingsService.Serialize(CurrentSettings()));
    }

    [RelayCommand(CanExecute = nameof(CanFetchLibrary))]
    private async Task ExportToCsv()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
        DismissResult();
        PersistSettings();
        try
        {
            var result = await _library.FetchAsync(
                SteamApiKey, SteamId, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);
            if (!await HandleFetchedLibraryAsync(result, "Export to CSV")) return;

            var file = await _dialogs.PickSaveCsvAsync("dbgame.csv");
            if (file == null) return;

            // Export just writes a fresh file with the fetched games — no merging.
            await _csv.WriteAsync(file, CsvGameExportService.Serialize(result.Rows));
            _log.WriteInfo($"CSV created with {result.Rows.Count} rows.");
            ShowResult($"✓ Saved {result.Rows.Count} games to {file.Name}", "Open file", OpenFileAction(file));
            RememberCsvFile(file);
            RecordSync(SyncHistory.Csv);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.WriteException(ex, "Steam API authorization error");
            await _dialogs.ShowErrorAsync("Steam authorization error", ex);
        }
        catch (OperationCanceledException)
        {
            _log.WriteInfo("CSV export cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Error exporting to CSV");
            LogFailure(SyncHistory.Csv, ex);
            await _dialogs.ShowErrorAsync("CSV export error", ex);
        }
        finally
        {
            Progress.End();
        }
    }

    [RelayCommand(CanExecute = nameof(CanFetchLibrary))]
    private async Task UpdateCsv()
    {
        if (Progress.IsBusy) return;
        var file = await _dialogs.PickOpenCsvAsync("Select CSV File to Update");
        if (file == null) return;
        await UpdateCsvFileAsync(file);
    }

    /// <summary>
    /// Handles a file dropped onto the window: a SteamDb game list is updated like
    /// <c>Update .csv</c>, a settings file is imported, anything else is explained.
    /// </summary>
    public async Task OpenDroppedFileAsync(IStorageFile file)
    {
        if (Progress.IsBusy) return;
        DismissResult();
        try
        {
            if (!DroppedFile.HasSupportedExtension(file.Name))
            {
                await _dialogs.ShowErrorAsync("Unsupported file", new Exception(
                    $"\"{file.Name}\" isn't a .csv file. Drop a SteamDb game list to update it, " +
                    "or a saved settings file to import it."));
                return;
            }

            var size = (await file.GetBasicPropertiesAsync()).Size;
            var content = size > DroppedFile.MaxBytes ? null : await _csv.ReadAsync(file);
            switch (DroppedFile.Classify(content))
            {
                case DroppedFileKind.GameCsv:
                    if (!CanFetchLibrary)
                    {
                        await _dialogs.ShowErrorAsync("Update CSV", new Exception(LibraryHint ?? NoSourceHint));
                        return;
                    }

                    _log.WriteInfo($"Dropped game list {file.Name}: updating it.");
                    await UpdateCsvFileAsync(file);
                    break;
                case DroppedFileKind.Settings:
                    _log.WriteInfo($"Dropped settings file {file.Name}: importing it.");
                    ApplyImportedSettings(content!);
                    ShowResult($"✓ Imported settings from {file.Name}");
                    break;
                default:
                    await _dialogs.ShowErrorAsync("Unsupported file", new Exception(
                        $"\"{file.Name}\" is neither a SteamDb game list (expected header: " +
                        $"{CsvGameExportService.Header}) nor a saved settings file."));
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, $"Error opening dropped file {file.Name}");
            await _dialogs.ShowErrorAsync("Couldn't open the file", ex);
        }
    }

    private async Task UpdateCsvFileAsync(IStorageFile file)
    {
        Progress.Begin("Fetching games…");
        DismissResult();
        PersistSettings();
        try
        {
            // Validate the chosen file is a SteamDb CSV before touching it.
            var existingContent = await _csv.ReadAsync(file);
            var missingColumns = CsvGameExportService.GetMissingColumns(existingContent);
            if (missingColumns.Count > 0)
            {
                var message =
                    "The selected file is not a SteamDb CSV. Missing columns: " +
                    string.Join(", ", missingColumns) +
                    $".{Environment.NewLine}Expected header: {CsvGameExportService.Header}";
                _log.WriteWarning($"Update CSV aborted: missing columns {string.Join(", ", missingColumns)}.");
                await _dialogs.ShowErrorAsync("Update CSV", new Exception(message));
                return;
            }

            var result = await _library.FetchAsync(
                SteamApiKey, SteamId, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);
            if (!await HandleFetchedLibraryAsync(result, "Update CSV")) return;

            var (added, total, backup) = await _csv.WriteMergedAsync(file, existingContent, result.Rows);
            var backupNote = backup != null ? $" · previous version saved as {Path.GetFileName(backup)}" : "";
            ShowResult(
                (added == 0
                    ? $"✓ {file.Name} is already up to date ({total} games)"
                    : $"✓ Added {added} new games to {file.Name} ({total} total)") + backupNote,
                "Open file", OpenFileAction(file),
                "Undo", added > 0 ? UndoCsvUpdateAction(file, backup) : null);
            RememberCsvFile(file);
            RecordSync(SyncHistory.Csv);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.WriteException(ex, "Steam API authorization error");
            await _dialogs.ShowErrorAsync("Steam authorization error", ex);
        }
        catch (OperationCanceledException)
        {
            _log.WriteInfo("CSV update cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Error updating CSV");
            LogFailure(SyncHistory.Csv, ex);
            await _dialogs.ShowErrorAsync("CSV update error", ex);
        }
        finally
        {
            Progress.End();
        }
    }

    private static string GoneSuffix(int count) => count > 0 ? $" · {count} no longer owned" : string.Empty;

    private void RecordSync(string target)
    {
        _syncHistory.Record(target, DateTimeOffset.Now);
        _syncLog.Add(new SyncLogEntry(DateTimeOffset.Now, target, true,
            (ResultMessage ?? "Done").TrimStart('✓', ' '), SyncLog.Manual));
        RefreshLastSync();
    }

    private void LogFailure(string target, Exception ex) =>
        _syncLog.Add(new SyncLogEntry(DateTimeOffset.Now, target, false, ex.Message, SyncLog.Manual));

    // The CSV just written becomes the one CSV auto-sync (and `--sync csv` without --file) updates.
    private void RememberCsvFile(IStorageFile file)
    {
        if (file.TryGetLocalPath() is not { } path) return;
        CsvFilePath = path;
        PersistSettings();
    }

    /// <summary>Opens the list of recent sync runs (manual, auto-sync, tray and command line).</summary>
    [RelayCommand]
    public void ShowSyncHistory() => _dialogs.ShowSyncLog(new SyncLogViewModel(_syncLog));

    private void RefreshLastSync()
    {
        var now = DateTimeOffset.Now;
        CsvLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.Csv), now);
        NotionLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.Notion), now);
        SheetsLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.GoogleSheets), now);
        NotionProgress = _backlog.Get(SyncHistory.Notion)?.Text;
        SheetsProgress = _backlog.Get(SyncHistory.GoogleSheets)?.Text;
    }

    private void ShowResult(
        string message, string? actionLabel = null, Action? action = null,
        string? secondaryLabel = null, Func<Task>? secondaryAction = null)
    {
        ResultMessage = message;
        ResultActionLabel = action != null ? actionLabel : null;
        _resultAction = action;
        ResultSecondaryLabel = secondaryAction != null ? secondaryLabel : null;
        _resultSecondaryAction = secondaryAction;
    }

    [RelayCommand]
    private void RunResultAction() => _resultAction?.Invoke();

    [RelayCommand]
    private async Task RunResultSecondaryAction()
    {
        if (_resultSecondaryAction != null) await _resultSecondaryAction();
    }

    // Undo for Update .csv: the .bak written just before goes back over the file.
    private Func<Task>? UndoCsvUpdateAction(IStorageFile file, string? backup)
    {
        var path = file.TryGetLocalPath();
        if (path == null || backup == null) return null;

        return async () =>
        {
            try
            {
                if (await CsvBackup.RestoreAsync(path))
                {
                    _log.WriteInfo($"Update CSV undone: restored {path} from its backup.");
                    ShowResult($"↶ Restored the previous version of {file.Name}", "Open file", OpenFileAction(file));
                }
                else
                {
                    ShowResult($"⚠ The backup of {file.Name} is gone, so the update can't be undone");
                }
            }
            catch (Exception ex)
            {
                _log.WriteException(ex, "Error restoring the CSV backup");
                await _dialogs.ShowErrorAsync("Undo CSV update", ex);
            }
        };
    }

    [RelayCommand]
    private void DismissResult()
    {
        ResultMessage = null;
        ResultActionLabel = null;
        _resultAction = null;
        ResultSecondaryLabel = null;
        _resultSecondaryAction = null;
    }

    // Opens a just-written file in its default app (Excel etc.); null when it has no local path.
    private Action? OpenFileAction(IStorageFile file)
    {
        var path = file.TryGetLocalPath();
        return path != null ? () => _dialogs.OpenLink(path) : null;
    }

    private Action? NotionDatabaseAction()
    {
        var id = DbId?.Trim().Replace("-", "");
        return string.IsNullOrEmpty(id) ? null : () => _dialogs.OpenLink($"https://www.notion.so/{id}");
    }

    // Shared post-fetch handling: syncs the connect rows, warns about expired sessions and, when
    // nothing was fetched, explains why. Returns false when there is nothing to export.
    private async Task<bool> HandleFetchedLibraryAsync(GameLibraryResult result, string title)
    {
        ApplyStoreStatuses(result);
        await NotifyExpiredSessionsAsync(result);

        if (result.Rows.Count > 0) return true;

        _log.WriteInfo("No games found to export.");
        if (!result.EpicSessionExpired && !result.GogSessionExpired && !result.XboxSessionExpired)
            await _dialogs.ShowErrorAsync(title,
                new Exception(
                    "No games found to export. Check your Steam credentials or Epic / GOG / Xbox connection."));
        return false;
    }

    // Reflects the latest cached-session outcome from a library fetch back onto the connect buttons.
    private void ApplyStoreStatuses(GameLibraryResult result)
    {
        Epic.IsConnected = result.EpicAuthenticated;
        Gog.IsConnected = result.GogAuthenticated;
        Xbox.IsConnected = result.XboxAuthenticated;
    }

    // After an interactive Xbox connect, fetch the library once and log the count — a live check
    // that both the auth chain and the data calls work. Not wired into export yet.
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

    // Surfaces a clear message when a previously connected store session has expired,
    // so the export isn't silently missing that store's games.
    private Task NotifyExpiredSessionsAsync(GameLibraryResult result)
    {
        var expired = new[]
        {
            (Store: "Epic", Expired: result.EpicSessionExpired),
            (Store: "GOG", Expired: result.GogSessionExpired),
            (Store: "Xbox", Expired: result.XboxSessionExpired)
        }.Where(s => s.Expired).Select(s => s.Store).ToList();
        if (expired.Count == 0) return Task.CompletedTask;

        foreach (var store in expired)
            _log.WriteWarning($"{store}: session expired — {store} games were skipped.");

        // One dialog for all of them, rather than one per store.
        var stores = expired.Count == 1 ? expired[0] : string.Join(", ", expired[..^1]) + " and " + expired[^1];
        var connect = string.Join(" / ", expired.Select(s => $"\"Connect {s}\""));
        return _dialogs.ShowErrorAsync(expired.Count == 1 ? $"{stores} session expired" : "Store sessions expired",
            new Exception($"Your {stores} {(expired.Count == 1 ? "session has" : "sessions have")} expired, " +
                          $"so those games were skipped. Click {connect} to log in again."));
    }

    [RelayCommand(CanExecute = nameof(CanFetchLibrary))]
    private async Task ViewLibrary()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
        DismissResult();
        PersistSettings();
        try
        {
            var result = await _library.FetchAsync(
                SteamApiKey, SteamId, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);
            if (!await HandleFetchedLibraryAsync(result, "Game library")) return;

            _dialogs.ShowLibrary(new LibraryViewModel(
                result.Rows, result.Ignored, _ignored, _backlog.Latest(), _dialogs.OpenLink));
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.WriteException(ex, "Steam API authorization error");
            await _dialogs.ShowErrorAsync("Steam authorization error", ex);
        }
        catch (OperationCanceledException)
        {
            _log.WriteInfo("Library fetch cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Error fetching library");
            await _dialogs.ShowErrorAsync("Library error", ex);
        }
        finally
        {
            Progress.End();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportNotion))]
    private async Task ExportToNotion()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
        DismissResult();
        PersistSettings();
        try
        {
            // Dry run first: nothing is written until the user confirms the preview.
            var plan = await _notionExporter.PlanAsync(
                SteamApiKey, SteamId, NotionToken, DbId, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);
            if (!await HandleFetchedLibraryAsync(plan.Library, "Export to Notion")) return;

            if (plan.IsEmpty)
            {
                // Nothing to write, but this still records the backlog progress read by the plan.
                await _notionExporter.ApplyAsync(NotionToken, DbId, plan, ct: Progress.Token);
                ShowResult($"✓ Notion is already up to date ({plan.Unchanged} games)" + GoneSuffix(plan.NoLongerOwned.Count),
                    "Open database", NotionDatabaseAction());
                RecordSync(SyncHistory.Notion);
                return;
            }

            Progress.SetIndeterminate("Waiting for confirmation…");
            if (!await _dialogs.ConfirmNotionExportAsync(new NotionExportPreviewViewModel(plan)))
            {
                _log.WriteInfo("Notion export not confirmed; nothing written.");
                return;
            }

            await _notionExporter.ApplyAsync(
                NotionToken, DbId, plan, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);
            ShowResult(
                plan.ToCreate.Count + plan.ToUpdate.Count == 0
                    ? "✓ Notion is up to date"
                    : $"✓ Notion: {plan.ToCreate.Count} added, {plan.ToUpdate.Count} updated" +
                      GoneSuffix(plan.NoLongerOwned.Count),
                "Open database", NotionDatabaseAction());
            RecordSync(SyncHistory.Notion);
        }
        catch (OperationCanceledException)
        {
            _log.WriteInfo("Notion export cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteError($"Data Export Failed to Notion: {ex.Message}");
            LogFailure(SyncHistory.Notion, ex);
            await _dialogs.ShowErrorAsync("Export error in Notion", ex);
        }
        finally
        {
            Progress.End();
        }
    }

    /// <summary>Finds pages of the same game in Notion and, after confirmation, merges them.</summary>
    [RelayCommand(CanExecute = nameof(CanFindNotionDuplicates))]
    private async Task FindNotionDuplicates()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Reading the Notion database…");
        DismissResult();
        PersistSettings();
        try
        {
            var groups = await _notionExporter.FindDuplicatesAsync(NotionToken, DbId, Progress.Token);
            if (groups.Count == 0)
            {
                ShowResult("✓ No duplicate pages in Notion", "Open database", NotionDatabaseAction());
                return;
            }

            Progress.SetIndeterminate("Waiting for confirmation…");
            if (!await _dialogs.ConfirmNotionDuplicatesAsync(new NotionDuplicatesViewModel(groups)))
            {
                _log.WriteInfo("Notion duplicate merge not confirmed; nothing changed.");
                return;
            }

            await _notionExporter.MergeDuplicatesAsync(NotionToken, DbId, groups, Progress.CreateReporter(), Progress.Token);
            var trashed = groups.Sum(g => g.Remove.Count);
            ShowResult($"✓ Merged {groups.Count} duplicate games · {trashed} pages moved to Notion's trash",
                "Open database", NotionDatabaseAction());
        }
        catch (OperationCanceledException)
        {
            _log.WriteInfo("Notion duplicate search cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Error merging Notion duplicates");
            await _dialogs.ShowErrorAsync("Notion duplicates error", ex);
        }
        finally
        {
            Progress.End();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportSheets))]
    private async Task ExportToGoogleSheets()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
        DismissResult();
        PersistSettings();
        try
        {
            var result = await _sheetsExporter.ExportAsync(
                GoogleSheetsTableName, SteamApiKey, SteamId, Progress.CreateReporter(), Progress.SetIndeterminate,
                Progress.Token);
            if (!await HandleFetchedLibraryAsync(result.Library, "Export to Google Sheets")) return;

            var url = result.SpreadsheetUrl;
            ShowResult(
                result.Added + result.Updated == 0
                    ? "✓ Google Sheets is already up to date"
                    : $"✓ Google Sheets: {result.Added} added, {result.Updated} updated" +
                      GoneSuffix(result.NoLongerOwned),
                url != null ? "Open sheet" : null, url != null ? () => _dialogs.OpenLink(url) : null);
            RecordSync(SyncHistory.GoogleSheets);
        }
        catch (OperationCanceledException)
        {
            _log.WriteInfo("Google Sheets export cancelled by user.");
        }
        catch (Exception ex)
        {
            _log.WriteException(ex, "Error exporting to Google Sheets");
            LogFailure(SyncHistory.GoogleSheets, ex);
            await _dialogs.ShowErrorAsync("Google Sheets export error", ex);
        }
        finally
        {
            Progress.End();
        }
    }

    [RelayCommand]
    private void OpenSteamApiKeyInfo()
    {
        _dialogs.OpenLink("https://steamcommunity.com/dev/apikey");
    }

    [RelayCommand]
    private void OpenInfoLinkSteamId()
    {
        _dialogs.OpenLink("https://github.com/AleksandrPidlozhevich/SteamDb#steam");
    }

    [RelayCommand]
    private void OpenInfoLinkNotionToken()
    {
        _dialogs.OpenLink("https://www.notion.so/profile/integrations");
    }

    [RelayCommand]
    private void OpenInfoLinkNotionDbId()
    {
        _dialogs.OpenLink("https://developers.notion.com/reference/retrieve-database");
    }

    [RelayCommand]
    private void OpenInfoGoogleSheets()
    {
        _dialogs.OpenLink("https://github.com/AleksandrPidlozhevich/SteamDb#google-sheets-beta");
    }

    [RelayCommand]
    public void OpenLinkKofi()
    {
        _dialogs.OpenLink("https://ko-fi.com/aliaksandrpidlazhevich");
    }

}
