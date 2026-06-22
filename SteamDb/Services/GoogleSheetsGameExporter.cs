using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using SteamDb.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>Appends new Steam games to a Google Sheet, creating it if needed. Beta / may be unstable.</summary>
public sealed class GoogleSheetsGameExporter
{
    private readonly ISecretStore _secrets;
    private readonly ILogService _log;

    public GoogleSheetsGameExporter(ISecretStore secrets, ILogService log)
    {
        _secrets = secrets;
        _log = log;
    }

    public async Task ExportAsync(
        string? tableName, string? steamApiKey, string? steamId, CancellationToken ct = default)
    {
        var client = new GoogleSheetsApiClient(_secrets, _log);

        if (!await client.ConnectAsync() || client.DriveService == null || client.SheetsService == null)
        {
            _log.WriteError("Authorization failed.");
            return;
        }

        var driveService = client.DriveService;
        var sheetsService = client.SheetsService;

        var spreadsheetName = tableName;
        string? spreadsheetId;

        var listRequest = driveService.Files.List();
        listRequest.Q =
            $"mimeType='application/vnd.google-apps.spreadsheet' and name='{spreadsheetName}' and trashed = false";
        listRequest.Spaces = "drive";
        listRequest.Fields = "files(id, name)";
        var fileList = await listRequest.ExecuteAsync(ct);

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

            var file = await driveService.Files.Create(fileMetadata).ExecuteAsync(ct);
            spreadsheetId = file.Id;
        }

        var existingDataRequest = sheetsService.Spreadsheets.Values.Get(spreadsheetId, "Sheet1!A2:B");
        var existingDataResponse = await existingDataRequest.ExecuteAsync(ct);

        var existingGameIds =
            new HashSet<string>(
                (existingDataResponse.Values?.Select(row => row.Count > 1 ? row[1].ToString() : null)
                    .Where(id => !string.IsNullOrEmpty(id)) ?? Enumerable.Empty<string>())!);

        var steamApiClient = new SteamApiClient(_log);
        var steamGamesResponse = await steamApiClient.GetOwnedGames(steamId, steamApiKey, ct);

        if (steamGamesResponse?.Response?.Games == null || !steamGamesResponse.Response.Games.Any())
        {
            _log.WriteError("Failed to receive a steam game list.");
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
            _log.WriteLog("All games already exist in the table.");
            return;
        }

        values.AddRange(newGames.Select(game => new List<object> { game.Name, game.GameID.ToString(), false }));

        var appendRequest = sheetsService.Spreadsheets.Values.Append(
            new ValueRange { Values = values.Skip(1).ToList() },
            spreadsheetId,
            "Sheet1!A1");
        appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
        await appendRequest.ExecuteAsync(ct);

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

        await sheetsService.Spreadsheets
            .BatchUpdate(batchUpdateRequest, spreadsheetId)
            .ExecuteAsync(ct);
    }
}