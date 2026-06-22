using SteamDb.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamDb.Services;

public sealed record GameLibraryResult(
    List<CsvGameExportRow> Rows,
    bool EpicAuthenticated,
    bool EpicSessionExpired,
    bool GogAuthenticated,
    bool GogSessionExpired,
    bool XboxAuthenticated,
    bool XboxSessionExpired);

/// <summary>
/// Fetches the owned games from Steam and/or Epic and/or GOG and/or Xbox and flattens them into
/// CSV rows. Network/auth concerns live here; the caller supplies progress and status callbacks.
/// </summary>
public sealed class GameLibraryService
{
    private readonly IStoreClientFactory _clients;

    public GameLibraryService(IStoreClientFactory clients)
    {
        _clients = clients;
    }

    public async Task<GameLibraryResult> FetchAsync(
        string? steamApiKey,
        string? steamId,
        IProgress<StoreFetchProgress>? progress = null,
        Action<string>? onStatus = null)
    {
        var hasSteamCredentials = !string.IsNullOrWhiteSpace(steamApiKey) && !string.IsNullOrWhiteSpace(steamId);

        var epicClient = _clients.CreateEpic();
        var gogClient = _clients.CreateGog();
        var xboxClient = _clients.CreateXbox();
        Task<SteamGamesResponse>? steamTask = null;

        if (hasSteamCredentials)
        {
            onStatus?.Invoke("Loading Steam library…");
            steamTask = new SteamApiClient().GetOwnedGames(steamId, steamApiKey);
        }

        var epicStatus = await epicClient.TryAuthenticateFromCacheAsync();
        var isEpicAuthenticated = epicStatus == StoreAuthFromCacheStatus.Authenticated;

        var gogStatus = await gogClient.TryAuthenticateFromCacheAsync();
        var isGogAuthenticated = gogStatus == StoreAuthFromCacheStatus.Authenticated;

        var xboxStatus = await xboxClient.TryAuthenticateFromCacheAsync();
        var isXboxAuthenticated = xboxStatus == StoreAuthFromCacheStatus.Authenticated;

        List<SteamGame>? steamGames = null;
        if (steamTask != null)
        {
            onStatus?.Invoke("Loading Steam library…");
            var steamGamesResponse = await steamTask;

            if (steamGamesResponse?.Response?.Games == null)
                throw new Exception("Failed to receive a list of games from Steam.");

            steamGames = steamGamesResponse.Response.Games;
        }

        List<EpicGame>? epicGames = null;
        if (isEpicAuthenticated)
        {
            onStatus?.Invoke("Loading Epic library…");
            epicGames = await epicClient.GetOwnedGamesAsync(progress);
        }

        List<GogGame>? gogGames = null;
        if (isGogAuthenticated)
        {
            onStatus?.Invoke("Loading GOG library…");
            gogGames = await gogClient.GetOwnedGamesAsync(progress);
        }

        List<XboxGame>? xboxGames = null;
        if (isXboxAuthenticated)
        {
            onStatus?.Invoke("Loading Xbox library…");
            xboxGames = await xboxClient.GetGamesAsync(progress);
        }

        if (!hasSteamCredentials && !isEpicAuthenticated && !isGogAuthenticated && !isXboxAuthenticated)
            throw new Exception("Please enter Steam credentials or connect Epic / GOG / Xbox first.");

        return new GameLibraryResult(
            CsvGameExportService.BuildFromLibraries(steamGames, epicGames, gogGames, xboxGames),
            isEpicAuthenticated,
            epicStatus == StoreAuthFromCacheStatus.SessionExpired,
            isGogAuthenticated,
            gogStatus == StoreAuthFromCacheStatus.SessionExpired,
            isXboxAuthenticated,
            xboxStatus == StoreAuthFromCacheStatus.SessionExpired);
    }
}