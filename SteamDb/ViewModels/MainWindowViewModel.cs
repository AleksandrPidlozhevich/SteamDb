using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDb.Models;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEpicConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowEpicCodeInput))]
    private bool isEpicConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEpicConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowEpicCodeInput))]
    private bool isEpicCodeInputVisible;

    [ObservableProperty] private string? epicAuthorizationCode;

    /// <summary>Initial state: show the compact "Connect Epic" button.</summary>
    public bool ShowEpicConnectButton => !IsEpicConnected && !IsEpicCodeInputVisible;

    /// <summary>After clicking Connect: show the authorization-code field.</summary>
    public bool ShowEpicCodeInput => !IsEpicConnected && IsEpicCodeInputVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGogConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowGogCodeInput))]
    private bool isGogConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGogConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowGogCodeInput))]
    private bool isGogCodeInputVisible;

    [ObservableProperty] private string? gogAuthorizationCode;

    /// <summary>Initial state: show the compact "Connect GOG" button.</summary>
    public bool ShowGogConnectButton => !IsGogConnected && !IsGogCodeInputVisible;

    /// <summary>After clicking Connect: show the authorization-code field.</summary>
    public bool ShowGogCodeInput => !IsGogConnected && IsGogCodeInputVisible;

    [ObservableProperty] private bool isBusy;

    [ObservableProperty] private double progressValue;

    [ObservableProperty] private double progressMaximum = 1;

    [ObservableProperty] private bool progressIsIndeterminate;

    [ObservableProperty] private string? progressStatus;

    private readonly StoreConnector _epicConnector;
    private readonly StoreConnector _gogConnector;

    public MainWindowViewModel()
    {
        LogService.Initialize(nameof(MainWindowViewModel));

        _epicConnector = new StoreConnector(
            "Epic",
            () => new EpicApiClient(),
            EpicAuthCodeParser.Extract,
            v => IsEpicConnected = v,
            v => IsEpicCodeInputVisible = v,
            c => EpicAuthorizationCode = c,
            () => IsEpicConnected,
            () => MainWindow?.Clipboard,
            ShowErrorAsync);

        _gogConnector = new StoreConnector(
            "GOG",
            () => new GogApiClient(),
            GogAuthCodeParser.Extract,
            v => IsGogConnected = v,
            v => IsGogCodeInputVisible = v,
            c => GogAuthorizationCode = c,
            () => IsGogConnected,
            () => MainWindow?.Clipboard,
            ShowErrorAsync);

        _ = _epicConnector.InitializeFromCacheAsync();
        _ = _gogConnector.InitializeFromCacheAsync();
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        var file = await PickSaveCsvAsync("seting_games.csv");
        if (file == null) return;

        var content = AppSettingsService.Serialize(new AppSettings(SteamApiKey, SteamId, NotionToken, DbId));
        await CsvFileService.WriteAsync(file, content);
    }

    [RelayCommand]
    private async Task ImportSettings()
    {
        var file = await PickOpenCsvAsync("Import CSV File");
        if (file == null) return;

        var content = await CsvFileService.ReadAsync(file);
        if (content == null) return;

        // Only overwrite the fields actually present in the file.
        var settings = AppSettingsService.Parse(content);
        SteamApiKey = settings.SteamApiKey ?? SteamApiKey;
        SteamId = settings.SteamId ?? SteamId;
        NotionToken = settings.NotionToken ?? NotionToken;
        DbId = settings.DbId ?? DbId;
    }

    [RelayCommand]
    private async Task ExportToCsv()
    {
        if (IsBusy) return;
        BeginBusy("Fetching games…");
        try
        {
            var result = await new GameLibraryService()
                .FetchAsync(SteamApiKey, SteamId, CreateStoreProgress(), SetIndeterminate);
            IsEpicConnected = result.EpicAuthenticated;
            IsGogConnected = result.GogAuthenticated;
            await NotifyStoreSessionExpiredAsync("Epic", result.EpicSessionExpired);
            await NotifyStoreSessionExpiredAsync("GOG", result.GogSessionExpired);

            if (result.Rows.Count == 0)
            {
                LogService.WriteInfo("No games found to export.");
                if (!result.EpicSessionExpired && !result.GogSessionExpired)
                    await ShowErrorAsync("Export to CSV",
                        new Exception("No games found to export. Check your Steam credentials or Epic / GOG connection."));
                return;
            }

            var file = await PickSaveCsvAsync("dbgame.csv");
            if (file == null) return;

            // Export just writes a fresh file with the fetched games — no merging.
            await CsvFileService.WriteAsync(file, CsvGameExportService.Serialize(result.Rows));
            LogService.WriteInfo($"CSV created with {result.Rows.Count} rows.");
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.WriteException(ex, "Steam API authorization error");
            await ShowErrorAsync("Steam authorization error", ex);
        }
        catch (Exception ex)
        {
            LogService.WriteException(ex, "Error exporting to CSV");
            await ShowErrorAsync("CSV export error", ex);
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
            var file = await PickOpenCsvAsync("Select CSV File to Update");
            if (file == null) return;

            // Validate the chosen file is a SteamDb CSV before touching it.
            var existingContent = await CsvFileService.ReadAsync(file);
            var missingColumns = CsvGameExportService.GetMissingColumns(existingContent);
            if (missingColumns.Count > 0)
            {
                var message =
                    "The selected file is not a SteamDb CSV. Missing columns: " +
                    string.Join(", ", missingColumns) +
                    $".{Environment.NewLine}Expected header: {CsvGameExportService.Header}";
                LogService.WriteWarning($"Update CSV aborted: missing columns {string.Join(", ", missingColumns)}.");
                await ShowErrorAsync("Update CSV", new Exception(message));
                return;
            }

            var result = await new GameLibraryService()
                .FetchAsync(SteamApiKey, SteamId, CreateStoreProgress(), SetIndeterminate);
            IsEpicConnected = result.EpicAuthenticated;
            IsGogConnected = result.GogAuthenticated;
            await NotifyStoreSessionExpiredAsync("Epic", result.EpicSessionExpired);
            await NotifyStoreSessionExpiredAsync("GOG", result.GogSessionExpired);

            if (result.Rows.Count == 0)
            {
                LogService.WriteInfo("No games found to export.");
                if (!result.EpicSessionExpired && !result.GogSessionExpired)
                    await ShowErrorAsync("Update CSV",
                        new Exception("No games found to export. Check your Steam credentials or Epic / GOG connection."));
                return;
            }

            await CsvFileService.WriteMergedAsync(file, existingContent, result.Rows);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.WriteException(ex, "Steam API authorization error");
            await ShowErrorAsync("Steam authorization error", ex);
        }
        catch (Exception ex)
        {
            LogService.WriteException(ex, "Error updating CSV");
            await ShowErrorAsync("CSV update error", ex);
        }
        finally
        {
            EndBusy();
        }
    }

    // Epic / GOG connect flow is shared — see StoreConnector. These thin members just
    // bind the commands and the generated OnXChanged hooks to the right connector.

    [RelayCommand]
    private Task StartEpicConnect() => _epicConnector.StartConnectAsync();

    partial void OnEpicAuthorizationCodeChanged(string? value) => _epicConnector.OnCodeChanged(value);

    [RelayCommand]
    private Task StartGogConnect() => _gogConnector.StartConnectAsync();

    partial void OnGogAuthorizationCodeChanged(string? value) => _gogConnector.OnCodeChanged(value);

    // Surfaces a clear message when a previously connected store session has expired,
    // so the export isn't silently missing that store's games.
    private Task NotifyStoreSessionExpiredAsync(string store, bool expired)
    {
        if (!expired) return Task.CompletedTask;

        LogService.WriteWarning($"{store}: session expired — {store} games were skipped.");
        return ShowErrorAsync($"{store} session expired",
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
            await new NotionGameExporter()
                .ExportAsync(SteamApiKey, SteamId, NotionToken, DbId, CreateStoreProgress(), SetIndeterminate);
        }
        catch (Exception ex)
        {
            LogService.WriteError($"Data Export Failed to Notion: {ex.Message}");
            await ShowErrorAsync("Export error in Notion", ex);
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task ExportToGoogleSheets()
    {
        try
        {
            await new GoogleSheetsGameExporter().ExportAsync(GoogleSheetsTableName, SteamApiKey, SteamId);
        }
        catch (Exception ex)
        {
            LogService.WriteException(ex, "Error exporting to Google Sheets");
            await ShowErrorAsync("Google Sheets export error", ex);
        }
    }

    [RelayCommand]
    private void OpenSteamApiKeyInfo() => LinkOpening("https://steamcommunity.com/dev/apikey");

    [RelayCommand]
    private void OpenInfoLinkSteamId() => LinkOpening("https://github.com/AleksandrPidlozhevich/SteamDb#");

    [RelayCommand]
    private void OpenInfoLinkNotionToken() => LinkOpening("https://www.notion.so/profile/integrations");

    [RelayCommand]
    private void OpenInfoLinkNotionDbId() => LinkOpening("https://developers.notion.com/reference/retrieve-a-database/");

    [RelayCommand]
    private void OpenInfoGoogleSheets() => LinkOpening("https://github.com/AleksandrPidlozhevich/SteamDb#");

    [RelayCommand]
    public void OpenLinkKofi() => LinkOpening("https://ko-fi.com/aliaksandrpidlazhevich");

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

    private void BeginBusy(string status)
    {
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
    }

    // ---- UI helpers ------------------------------------------------------------------

    private static Window? MainWindow =>
        (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static readonly FilePickerFileType CsvFileType =
        new("CSV files") { Patterns = ["*.csv"] };

    private static async Task<IStorageFile?> PickSaveCsvAsync(string suggestedName)
    {
        var window = MainWindow;
        if (window == null) return null;

        return await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            Title = "Save CSV File",
            FileTypeChoices = [CsvFileType]
        });
    }

    private static async Task<IStorageFile?> PickOpenCsvAsync(string title)
    {
        var window = MainWindow;
        if (window == null) return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [CsvFileType]
        });

        return files.FirstOrDefault();
    }

    private static void LinkOpening(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", url);
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
    }

    private async Task ShowErrorAsync(string title, Exception exception)
    {
        var window = MainWindow;
        if (window == null) return;

        var errorWindow = new Error(title, BuildErrorMessage(exception));
        await errorWindow.ShowDialog(window);
    }

    private static string BuildErrorMessage(Exception exception)
    {
        var messages = new List<string>();
        var current = exception;
        while (current != null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.Message);
            current = current.InnerException;
        }

        return messages.Count == 0 ? "Unknown error" : string.Join(Environment.NewLine, messages.Distinct());
    }
}
