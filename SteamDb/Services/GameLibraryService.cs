using SteamDb.Models;
using System;
using System.Collections.Generic;
using System.Threading;
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
    private readonly ILogService _log;

    public GameLibraryService(IStoreClientFactory clients, ILogService log)
    {
        _clients = clients;
        _log = log;
    }

    public async Task<GameLibraryResult> FetchAsync(
        string? steamApiKey,
        string? steamId,
        IProgress<StoreFetchProgress>? progress = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var hasSteamCredentials = !string.IsNullOrWhiteSpace(steamApiKey) && !string.IsNullOrWhiteSpace(steamId);

        var epicClient = _clients.CreateEpic();
        var gogClient = _clients.CreateGog();
        var xboxClient = _clients.CreateXbox();
        Task<SteamGamesResponse>? steamTask = null;

        if (hasSteamCredentials)
        {
            onStatus?.Invoke("Loading Steam library…");
            steamTask = new SteamApiClient(_log).GetOwnedGames(steamId, steamApiKey, ct);
        }

        // The three cached-session checks are independent network calls — run them together.
        var epicAuthTask = epicClient.TryAuthenticateFromCacheAsync();
        var gogAuthTask = gogClient.TryAuthenticateFromCacheAsync();
        var xboxAuthTask = xboxClient.TryAuthenticateFromCacheAsync();
        await Task.WhenAll(epicAuthTask, gogAuthTask, xboxAuthTask);

        var epicStatus = await epicAuthTask;
        var isEpicAuthenticated = epicStatus == StoreAuthFromCacheStatus.Authenticated;

        var gogStatus = await gogAuthTask;
        var isGogAuthenticated = gogStatus == StoreAuthFromCacheStatus.Authenticated;

        var xboxStatus = await xboxAuthTask;
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
            epicGames = await epicClient.GetOwnedGamesAsync(progress, ct);
        }

        List<GogGame>? gogGames = null;
        if (isGogAuthenticated)
        {
            onStatus?.Invoke("Loading GOG library…");
            gogGames = await gogClient.GetOwnedGamesAsync(progress, ct);
        }

        List<XboxGame>? xboxGames = null;
        if (isXboxAuthenticated)
        {
            onStatus?.Invoke("Loading Xbox library…");
            xboxGames = await xboxClient.GetGamesAsync(progress, ct);
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