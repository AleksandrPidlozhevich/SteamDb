using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDb.Models;
using SteamDb.Services;
using System;
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

    public bool HasResult => ResultMessage is not null;

    /// <summary>"Last sync: today 14:20" under each export button; null when never synced.</summary>
    [ObservableProperty] private string? csvLastSync;

    [ObservableProperty] private string? notionLastSync;

    [ObservableProperty] private string? sheetsLastSync;

    private readonly SyncHistory _syncHistory;
    private readonly StoreSelection _selection;

    /// <summary>"Include in export" ticks per store (saved; also honoured by <c>--sync</c>).</summary>
    [ObservableProperty] private bool includeSteam = true;

    [ObservableProperty] private bool includeEpic = true;

    [ObservableProperty] private bool includeGog = true;

    [ObservableProperty] private bool includeXbox = true;

    public bool HasResultAction => ResultActionLabel is not null;


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
        StoreSelection selection)
    {
        _selection = selection;
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

        _ = Epic.InitializeFromCacheAsync();
        _ = Gog.InitializeFromCacheAsync();
        _ = Xbox.InitializeFromCacheAsync();
        _ = CheckForUpdateAsync();
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
        var log = new FileLogService();
        var secrets = new MsalSecretStore(log);
        var clients = new StoreClientFactory(secrets, log);
        var selection = new StoreSelection();
        var library = new GameLibraryService(clients, selection);
        return new Dependencies(
            secrets,
            new EmbeddedWebViewAuthenticator(DefaultTopLevel),
            clients,
            library,
            new NotionGameExporter(library, new NotionGateway(log), log),
            new GoogleSheetsGameExporter(secrets, library, log),
            new WindowDialogService(),
            new CsvFileService(log),
            log,
            new VelopackUpdateService(log),
            selection);
    }

    private MainWindowViewModel(Dependencies d)
        : this(d.Secrets, d.WebAuth, d.Clients, d.Library, d.NotionExporter, d.SheetsExporter, d.Dialogs,
            d.Csv, d.Log, d.Updates, d.Selection)
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
        StoreSelection Selection);

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
    }

    private AppSettings CurrentSettings() =>
        new(SteamApiKey, SteamId, NotionToken, DbId, GoogleSheetsTableName, _selection.Serialize());

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

            var (added, total) = await _csv.WriteMergedAsync(file, existingContent, result.Rows);
            ShowResult(
                added == 0
                    ? $"✓ {file.Name} is already up to date ({total} games)"
                    : $"✓ Added {added} new games to {file.Name} ({total} total)",
                "Open file", OpenFileAction(file));
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
        RefreshLastSync();
    }

    private void RefreshLastSync()
    {
        var now = DateTimeOffset.Now;
        CsvLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.Csv), now);
        NotionLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.Notion), now);
        SheetsLastSync = SyncHistory.Describe(_syncHistory.Get(SyncHistory.GoogleSheets), now);
    }

    private void ShowResult(string message, string? actionLabel = null, Action? action = null)
    {
        ResultMessage = message;
        ResultActionLabel = action != null ? actionLabel : null;
        _resultAction = action;
    }

    [RelayCommand]
    private void RunResultAction() => _resultAction?.Invoke();

    [RelayCommand]
    private void DismissResult()
    {
        ResultMessage = null;
        ResultActionLabel = null;
        _resultAction = null;
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

            _dialogs.ShowLibrary(new LibraryViewModel(result.Rows));
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
            await _dialogs.ShowErrorAsync("Export error in Notion", ex);
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
