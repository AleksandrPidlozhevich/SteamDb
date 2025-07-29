using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using SteamDb.Models;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    public MainWindowViewModel()
    {
        LogService.Initialize(nameof(MainWindowViewModel));
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        var csvContent = $"Steam API Key: {SteamApiKey}" + Environment.NewLine +
                         $"Steam ID: {SteamId}" + Environment.NewLine +
                         $"NotionToken: {NotionToken}" + Environment.NewLine +
                         $"DbId: {DbId}" + Environment.NewLine;

        var mainWindow = (App.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var file = await mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "seting_games.csv",
            Title = "Save CSV File",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } }
            }
        });

        if (file != null)
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(csvContent);
        }
    }

    [RelayCommand]
    private async Task ExportToCsv()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SteamApiKey)) throw new Exception("Please enter steam api key");
            if (string.IsNullOrWhiteSpace(SteamId)) throw new Exception("Please enter Steam ID");

            var steamApiClient = new SteamApiClient();
            var steamGamesResponse = await steamApiClient.GetOwnedGames(SteamId, SteamApiKey);

            var csvContent = "Name,Game ID" + Environment.NewLine;
            foreach (var game in steamGamesResponse.Response.Games)
                csvContent += $"{game.Name},{game.GameID}" + Environment.NewLine;

            var mainWindow = (App.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow != null)
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filters = { new FileDialogFilter { Name = "CSV files", Extensions = { "csv" } } },
                    Title = "Save CSV File",
                    InitialFileName = "dbgame.csv"
                };
                var filePath = await saveFileDialog.ShowAsync(mainWindow);
                if (filePath != null) await File.WriteAllTextAsync(filePath, csvContent);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.WriteException(ex, "Steam API authorization error");
            throw new Exception("Steam Api Authorization Error.", ex);
        }
        catch (Exception ex)
        {
            LogService.WriteException(ex, "Error exporting to CSV");
            throw new Exception($"Export error: {ex.Message}", ex);
        }
    }

    [RelayCommand]
    private async Task ExportToNotion()
    {
        try
        {
            var steamApiClient = new SteamApiClient();
            var notionApiClient = new NotionApiClient(NotionToken, DbId);
            var notionDataFetcher = new NotionDataFetcher(notionApiClient);
            var steamTask = steamApiClient.GetOwnedGames(SteamId, SteamApiKey);
            var notionTask = notionDataFetcher.FetchGameDataAsync();

            await Task.WhenAll(steamTask, notionTask);

            var steamGamesResponse = await steamTask;
            var existingGames = await notionTask;

            if (steamGamesResponse?.Response?.Games == null)
                throw new Exception("Не вдалося отримати ігри зі Steam");

            var existingGameIds = new HashSet<int>(existingGames.Keys);

            var newGames = steamGamesResponse.Response.Games
                .Where(game => !existingGameIds.Contains(game.GameID))
                .Select(game => new
                {
                    parent = new { database_id = DbId },
                    properties = new
                    {
                        Name = new
                        {
                            title = new[]
                            {
                                new { text = new { content = game.Name } }
                            }
                        },
                        GameID = new
                        {
                            number = game.GameID
                        }
                    }
                })
                .ToList();

            if (!newGames.Any())
            {
                LogService.WriteInfo("New Games for Adding Not Found");
                return;
            }

            LogService.WriteInfo($"Found {newGames.Count} new games to add");

            var batches = newGames
                .Select((item, index) => new { item, index })
                .GroupBy(x => x.index / 10)
                .Select(g => g.Select(x => x.item).ToList())
                .ToList();

            var totalNewGames = newGames.Count;
            var addedGames = 0;

            var batchTasks = batches.Select(async (batch, batchIndex) =>
            {
                await notionApiClient.AddPagesToDatabaseParallel(batch);
                Interlocked.Add(ref addedGames, batch.Count);
                LogService.WriteInfo(
                    $"Batch {batchIndex + 1}: Added {batch.Count} games. Total:: {addedGames}/{totalNewGames}");
            });

            var semaphore = new SemaphoreSlim(2, 2);
            var throttledTasks = batchTasks.Select(async task =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await task;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(throttledTasks);

            LogService.WriteInfo($"Successfully added {totalNewGames} new games to Notion");
        }
        catch (Exception ex)
        {
            LogService.WriteError($"Data Export Failed to Notion: {ex.Message}");
            throw;
        }
    }

    [RelayCommand]
    private async Task ImportSettings()
    {
        var mainWindow = (App.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (mainWindow == null) return;

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import CSV File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null) return;

        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) continue;

            var keyValue = line.Split(": ", 2);
            if (keyValue.Length != 2) continue;

            switch (keyValue[0])
            {
                case "Steam API Key": SteamApiKey = keyValue[1]; break;
                case "Steam ID": SteamId = keyValue[1]; break;
                case "NotionToken": NotionToken = keyValue[1]; break;
                case "DbId": DbId = keyValue[1]; break;
            }
        }
    }

    [RelayCommand]
    private async Task ExportToGoogleSheets()
    {
        var client = new GoogleSheetsApiClient();

        if (!await client.ConnectAsync())
        {
            LogService.WriteError("Authorization failed.");
            return;
        }

        var spreadsheetName = GoogleSheetsTableName;
        string spreadsheetId = null;

        var listRequest = client.DriveService.Files.List();
        listRequest.Q =
            $"mimeType='application/vnd.google-apps.spreadsheet' and name='{spreadsheetName}' and trashed = false";
        listRequest.Spaces = "drive";
        listRequest.Fields = "files(id, name)";
        var fileList = await listRequest.ExecuteAsync();

        if (fileList.Files != null && fileList.Files.Count > 0)
        {
            spreadsheetId = fileList.Files[0].Id;
        }
        else
        {
            var fileMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = spreadsheetName,
                MimeType = "application/vnd.google-apps.spreadsheet"
            };

            var file = await client.DriveService.Files.Create(fileMetadata).ExecuteAsync();
            spreadsheetId = file.Id;
        }

        var existingDataRequest = client.SheetsService.Spreadsheets.Values.Get(spreadsheetId, "Sheet1!A2:B");
        var existingDataResponse = await existingDataRequest.ExecuteAsync();

        var existingGameIds =
            new HashSet<string>(
                (existingDataResponse.Values?.Select(row => row.Count > 1 ? row[1].ToString() : null)
                    .Where(id => !string.IsNullOrEmpty(id)) ?? Enumerable.Empty<string>())!);

        var steamApiClient = new SteamApiClient();
        var steamGamesResponse = await steamApiClient.GetOwnedGames(SteamId, SteamApiKey);

        if (steamGamesResponse?.Response?.Games == null || !steamGamesResponse.Response.Games.Any())
        {
            LogService.WriteError("Failed to receive a steam game list.");
            return;
        }

        var values = new List<IList<object>>
        {
            new List<object> { "Name", "Game ID", "Completed" }
        };

        var newGames = steamGamesResponse.Response.Games
            .Where(game => !existingGameIds.Contains(game.GameID.ToString()))
            .ToList();

        if (newGames.Count == 0)
        {
            LogService.WriteLog("All games already exist in the table.");
            return;
        }

        values.AddRange(newGames.Select(game => new List<object> { game.Name, game.GameID.ToString(), false }));

        var appendRequest = client.SheetsService.Spreadsheets.Values.Append(
            new ValueRange { Values = values.Skip(1).ToList() },
            spreadsheetId,
            "Sheet1!A1");
        appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
        await appendRequest.ExecuteAsync();

        var startRow = (existingDataResponse.Values?.Count ?? 0) + 1;
        var endRow = startRow + newGames.Count;

        var requests = new List<Request>
        {
            new()
            {
                SetDataValidation = new SetDataValidationRequest
                {
                    Range = new GridRange
                    {
                        SheetId = 0,
                        StartRowIndex = startRow,
                        EndRowIndex = endRow,
                        StartColumnIndex = 2,
                        EndColumnIndex = 3
                    },
                    Rule = new DataValidationRule
                    {
                        Condition = new BooleanCondition
                        {
                            Type = "BOOLEAN"
                        },
                        Strict = true,
                        ShowCustomUi = true
                    }
                }
            }
        };

        var batchUpdateRequest = new BatchUpdateSpreadsheetRequest
        {
            Requests = requests
        };

        await client.SheetsService.Spreadsheets
            .BatchUpdate(batchUpdateRequest, spreadsheetId)
            .ExecuteAsync();
    }

    [RelayCommand]
    private void OpenSteamApiKeyInfo()
    {
        LinkOpening("https://steamcommunity.com/dev/apikey");
    }

    [RelayCommand]
    private void OpenInfoLinkSteamId()
    {
        LinkOpening("https://github.com/AleksandrPidlozhevich/SteamDb#");
    }

    [RelayCommand]
    private void OpenInfoLinkNotionToken()
    {
        LinkOpening("https://www.notion.so/profile/integrations");
    }

    [RelayCommand]
    private void OpenInfoLinkNotionDbId()
    {
        LinkOpening("https://developers.notion.com/reference/retrieve-a-database/");
    }

    [RelayCommand]
    private void OpenInfoGoogleSheets()
    {
        LinkOpening("https://github.com/AleksandrPidlozhevich/SteamDb#");
    }

    [RelayCommand]
    public void OpenLinkKofi()
    {
        LinkOpening("https://ko-fi.com/aliaksandrpidlazhevich");
    }

    private void LinkOpening(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", url);
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
    }
}