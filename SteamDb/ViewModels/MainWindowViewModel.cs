using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDb.Models;
using SteamDb.Services;
using System;
using System.Linq;
using System.Threading;
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

    [ObservableProperty] private bool isBusy;

    [ObservableProperty] private double progressValue;

    [ObservableProperty] private double progressMaximum = 1;

    [ObservableProperty] private bool progressIsIndeterminate;

    [ObservableProperty] private string? progressStatus;

    // Cancels the in-flight export; created in BeginBusy, signalled by the Cancel command.
    private CancellationTokenSource? _cts;

    private const string SettingsSecretKey = "app-settings";

    private readonly ISecretStore _secrets;
    private readonly IWebAuthenticator _webAuth;
    private readonly IStoreClientFactory _clients;
    private readonly GameLibraryService _library;
    private readonly NotionGameExporter _notionExporter;
    private readonly GoogleSheetsGameExporter _sheetsExporter;
    private readonly IDialogService _dialogs;
    private readonly CsvFileService _csv;
    private readonly ILogService _log;

    public MainWindowViewModel(
        ISecretStore secrets,
        IWebAuthenticator webAuth,
        IStoreClientFactory clients,
        GameLibraryService library,
        NotionGameExporter notionExporter,
        GoogleSheetsGameExporter sheetsExporter,
        IDialogService dialogs,
        CsvFileService csv,
        ILogService log)
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
            codePlaceholder: "Epic authorization code");

        Gog = new StoreConnectionViewModel(
            "GOG",
            () => _clients.CreateGog(),
            GogAuthCodeParser.Extract,
            () => _dialogs.Clipboard,
            _dialogs.ShowErrorAsync,
            _log,
            _webAuth,
            connectingStatus: "Opening GOG login…",
            beginBusy: BeginBusy,
            endBusy: EndBusy);

        Xbox = new StoreConnectionViewModel(
            "Xbox",
            () => _clients.CreateXbox(),
            XboxAuthCodeParser.Extract,
            () => _dialogs.Clipboard,
            _dialogs.ShowErrorAsync,
            _log,
            _webAuth,
            connectingStatus: "Opening Xbox login…",
            beginBusy: BeginBusy,
            endBusy: EndBusy,
            onConnected: OnXboxConnectedAsync);

        _ = Epic.InitializeFromCacheAsync();
        _ = Gog.InitializeFromCacheAsync();
        _ = Xbox.InitializeFromCacheAsync();
    }

    // After a successful interactive Xbox connect, pull the library once (live check + logs counts).
    private async Task OnXboxConnectedAsync(StoreConnectionViewModel _)
    {
        SetIndeterminate("Loading Xbox library…");
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
            new NotionGameExporter(library, log),
            new GoogleSheetsGameExporter(secrets, log),
            new WindowDialogService(),
            new CsvFileService(log),
            log);
    }

    private MainWindowViewModel(Dependencies d)
        : this(d.Secrets, d.WebAuth, d.Clients, d.Library, d.NotionExporter, d.SheetsExporter, d.Dialogs,
            d.Csv, d.Log)
    {
    }

    private sealed record Dependencies(
        ISecretStore Secrets,
        IWebAuthenticator WebAuth,
        IStoreClientFactory Clients,
        GameLibraryService Library,
        NotionGameExporter NotionExporter,
        GoogleSheetsGameExporter SheetsExporter,
        IDialogService Dialogs,
        CsvFileService Csv,
        ILogService Log);

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
        if (IsBusy) return;
        BeginBusy("Fetching games…");
        try
        {
            var result = await _library.FetchAsync(
                SteamApiKey, SteamId, CreateStoreProgress(), SetIndeterminate, _cts!.Token);
            ApplyStoreStatuses(result);
            await NotifyStoreSessionExpiredAsync("Epic", result.EpicSessionExpired);
            await NotifyStoreSessionExpiredAsync("GOG", result.GogSessionExpired);
            await NotifyStoreSessionExpiredAsync("Xbox", result.XboxSessionExpired);

            if (result.Rows.Count == 0)
            {
                _log.WriteInfo("No games found to export.");
                if (!result.EpicSessionExpired && !result.GogSessionExpired && !result.XboxSessionExpired)
                    await _dialogs.ShowErrorAsync("Export to CSV",
                        new Exception(
                            "No games found to export. Check your Steam credentials or Epic / GOG / Xbox connection."));
                return;
            }

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
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task UpdateCsv()
    {
        if (IsBusy) return;
        BeginBusy("Fetching games…");
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
                SteamApiKey, SteamId, CreateStoreProgress(), SetIndeterminate, _cts!.Token);
            ApplyStoreStatuses(result);
            await NotifyStoreSessionExpiredAsync("Epic", result.EpicSessionExpired);
            await NotifyStoreSessionExpiredAsync("GOG", result.GogSessionExpired);
            await NotifyStoreSessionExpiredAsync("Xbox", result.XboxSessionExpired);

            if (result.Rows.Count == 0)
            {
                _log.WriteInfo("No games found to export.");
                if (!result.EpicSessionExpired && !result.GogSessionExpired && !result.XboxSessionExpired)
                    await _dialogs.ShowErrorAsync("Update CSV",
                        new Exception(
                            "No games found to export. Check your Steam credentials or Epic / GOG / Xbox connection."));
                return;
            }

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
            EndBusy();
        }
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
    private async Task ExportToNotion()
    {
        if (IsBusy) return;
        BeginBusy("Fetching games…");
        try
        {
            await _notionExporter.ExportAsync(
                SteamApiKey, SteamId, NotionToken, DbId, CreateStoreProgress(), SetIndeterminate, _cts!.Token);
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
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task ExportToGoogleSheets()
    {
        if (IsBusy) return;
        BeginBusy("Exporting to Google Sheets…");
        try
        {
            await _sheetsExporter.ExportAsync(GoogleSheetsTableName, SteamApiKey, SteamId, _cts!.Token);
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
            EndBusy();
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
        _dialogs.OpenLink("https://developers.notion.com/reference/retrieve-a-database/");
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

    // ---- Progress / busy state -------------------------------------------------------

    // One reporter for any store; the stage label comes from the progress payload.
    private IProgress<StoreFetchProgress> CreateStoreProgress()
    {
        // Constructed on the UI thread, so callbacks marshal back to it automatically.
        return new Progress<StoreFetchProgress>(p =>
        {
            var stage = string.IsNullOrEmpty(p.Stage) ? "Working" : p.Stage;

            if (p.Total <= 0)
            {
                ProgressIsIndeterminate = true;
                ProgressStatus = $"{stage}…";
                return;
            }

            ProgressIsIndeterminate = false;
            ProgressMaximum = p.Total;
            ProgressValue = p.Completed;
            ProgressStatus = $"{stage}… {p.Completed}/{p.Total}";
        });
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        ProgressStatus = "Cancelling…";
    }

    private void BeginBusy(string status)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressIsIndeterminate = true;
        ProgressValue = 0;
        ProgressMaximum = 1;
        ProgressStatus = status;
    }

    private void SetIndeterminate(string status)
    {
        ProgressIsIndeterminate = true;
        ProgressStatus = status;
    }

    private void EndBusy()
    {
        IsBusy = false;
        ProgressIsIndeterminate = false;
        ProgressValue = 0;
        ProgressStatus = null;
        _cts?.Dispose();
        _cts = null;
    }
}
