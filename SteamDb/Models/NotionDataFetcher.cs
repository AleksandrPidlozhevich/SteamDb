using Newtonsoft.Json.Linq;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamDb.Models;

internal class NotionDataFetcher
{
    private readonly NotionApiClient _notionApiClient;

    public NotionDataFetcher(NotionApiClient notionApiClient)
    {
        _notionApiClient = notionApiClient;
    }

    public async Task<Dictionary<int, string>> FetchGameDataAsync()
    {
        try
        {
            var allPages = await _notionApiClient.QueryAllPagesAsync();
            var gameData = allPages
                .AsParallel()
                .Where(page => page != null)
                .Select(page => ExtractGameData(page))
                .Where(data => data.HasValue)
                .ToDictionary(data => data.Value.GameId, data => data.Value.Name);
            return gameData;
        }
        catch (Exception ex)
        {
            LogService.WriteError($"Data Error with Notion: {ex.Message}");
            throw;
        }
    }

    private (int GameId, string Name)? ExtractGameData(JObject page)
    {
        try
        {
            var properties = page["properties"];
            var gameIdProperty = properties?["GameID"];
            var nameProperty = properties?["Name"];

            if (gameIdProperty?["number"] != null && nameProperty?["title"] != null)
            {
                var gameId = gameIdProperty["number"].Value<int>();
                var titleArray = nameProperty["title"] as JArray;

                if (titleArray != null && titleArray.Count > 0)
                {
                    var name = titleArray[0]["text"]["content"].Value<string>();
                    return (gameId, name);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.WriteError($"Page processing error {ex.Message}");
        }

        return null;
    }
}