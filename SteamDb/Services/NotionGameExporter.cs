using SteamDb.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>Pushes new Steam games into a Notion database, deduping against existing rows.</summary>
public sealed class NotionGameExporter
{
    public async Task ExportAsync(string? steamApiKey, string? steamId, string? notionToken, string? dbId)
    {
        var steamApiClient = new SteamApiClient();
        var notionApiClient = new NotionApiClient(notionToken, dbId);
        var notionDataFetcher = new NotionDataFetcher(notionApiClient);
        var steamTask = steamApiClient.GetOwnedGames(steamId, steamApiKey);
        var notionTask = notionDataFetcher.FetchGameDataAsync();

        await Task.WhenAll(steamTask, notionTask);

        var steamGamesResponse = await steamTask;
        var existingGames = await notionTask;

        if (steamGamesResponse?.Response?.Games == null)
            throw new Exception("Failed to receive a list of games from Steam.");

        var existingGameIds = new HashSet<int>(existingGames.Keys);

        var newGames = steamGamesResponse.Response.Games
            .Where(game => !existingGameIds.Contains(game.GameID))
            .Select(game => new
            {
                parent = new { database_id = dbId },
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
}
