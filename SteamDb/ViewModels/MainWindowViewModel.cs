using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDb.Models;
using SteamDb.Services;
using System;
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

    private const string SettingsSecretKey = "app-settings";

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
        IUpdateService updates)
    {
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

        LoadPersistedSettings();

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
        var library = new GameLibraryService(clients);
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
            new VelopackUpdateService(log));
    }

    private MainWindowViewModel(Dependencies d)
        : this(d.Secrets, d.WebAuth, d.Clients, d.Library, d.NotionExporter, d.SheetsExporter, d.Dialogs,
            d.Csv, d.Log, d.Updates)
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
        IUpdateService Updates);

    private static TopLevel? DefaultTopLevel() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    [RelayCommand]
    private async Task SaveSettings()
    {
        var file = await _dialogs.PickSaveCsvAsync("seting_games.csv");
        if (file == null) return;

        var content = AppSettingsService.Serialize(new AppSettings(SteamApiKey, SteamId, NotionToken, DbId));
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

        // Only overwrite the fields actually present in the file.
        var settings = AppSettingsService.Parse(content);
        SteamApiKey = settings.SteamApiKey ?? SteamApiKey;
        SteamId = settings.SteamId ?? SteamId;
        NotionToken = settings.NotionToken ?? NotionToken;
        DbId = settings.DbId ?? DbId;
        PersistSettings();
    }

    /// <summary>Loads credentials remembered (encrypted) from a previous session.</summary>
    private void LoadPersistedSettings()
    {
        var content = _secrets.Load(SettingsSecretKey);
        if (string.IsNullOrEmpty(content)) return;

        var settings = AppSettingsService.Parse(content);
        SteamApiKey = settings.SteamApiKey ?? SteamApiKey;
        SteamId = settings.SteamId ?? SteamId;
        NotionToken = settings.NotionToken ?? NotionToken;
        DbId = settings.DbId ?? DbId;
    }

    /// <summary>Persists the current credentials to the encrypted secret store.</summary>
    private void PersistSettings()
    {
        _secrets.Save(SettingsSecretKey,
            AppSettingsService.Serialize(new AppSettings(SteamApiKey, SteamId, NotionToken, DbId)));
    }

    [RelayCommand]
    private async Task ExportToCsv()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
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

    [RelayCommand]
    private async Task UpdateCsv()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
        try
        {
            var file = await _dialogs.PickOpenCsvAsync("Select CSV File to Update");
            if (file == null) return;

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

            await _csv.WriteMergedAsync(file, existingContent, result.Rows);
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

    // Shared post-fetch handling: syncs the connect rows, warns about expired sessions and, when
    // nothing was fetched, explains why. Returns false when there is nothing to export.
    private async Task<bool> HandleFetchedLibraryAsync(GameLibraryResult result, string title)
    {
        ApplyStoreStatuses(result);
        await NotifyStoreSessionExpiredAsync("Epic", result.EpicSessionExpired);
        await NotifyStoreSessionExpiredAsync("GOG", result.GogSessionExpired);
        await NotifyStoreSessionExpiredAsync("Xbox", result.XboxSessionExpired);

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
    private Task NotifyStoreSessionExpiredAsync(string store, bool expired)
    {
        if (!expired) return Task.CompletedTask;

        _log.WriteWarning($"{store}: session expired — {store} games were skipped.");
        return _dialogs.ShowErrorAsync($"{store} session expired",
            new Exception($"Your {store} session has expired, so {store} games were skipped. " +
                          $"Click \"Connect {store}\" to log in again."));
    }

    [RelayCommand]
    private async Task ViewLibrary()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
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

    [RelayCommand]
    private async Task ExportToNotion()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
        try
        {
            // Dry run first: nothing is written until the user confirms the preview.
            var plan = await _notionExporter.PlanAsync(
                SteamApiKey, SteamId, NotionToken, DbId, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);
            if (!await HandleFetchedLibraryAsync(plan.Library, "Export to Notion")) return;

            Progress.SetIndeterminate("Waiting for confirmation…");
            if (!await _dialogs.ConfirmNotionExportAsync(new NotionExportPreviewViewModel(plan)))
            {
                _log.WriteInfo("Notion export not confirmed; nothing written.");
                return;
            }

            await _notionExporter.ApplyAsync(
                NotionToken, DbId, plan, Progress.CreateReporter(), Progress.SetIndeterminate, Progress.Token);
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

    [RelayCommand]
    private async Task ExportToGoogleSheets()
    {
        if (Progress.IsBusy) return;
        Progress.Begin("Fetching games…");
        try
        {
            var result = await _sheetsExporter.ExportAsync(
                GoogleSheetsTableName, SteamApiKey, SteamId, Progress.CreateReporter(), Progress.SetIndeterminate,
                Progress.Token);
            if (!await HandleFetchedLibraryAsync(result.Library, "Export to Google Sheets")) return;

            // Show the result: open the synced spreadsheet in the browser.
            if (result.SpreadsheetUrl != null)
                _dialogs.OpenLink(result.SpreadsheetUrl);
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
        _dialogs.OpenLink("https://github.com/AleksandrPidlozhevich/SteamDb#");
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
        _dialogs.OpenLink("https://github.com/AleksandrPidlozhevich/SteamDb#");
    }

    [RelayCommand]
    public void OpenLinkKofi()
    {
        _dialogs.OpenLink("https://ko-fi.com/aliaksandrpidlazhevich");
    }

}
