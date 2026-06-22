using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamDb.Models;
using SteamDb.Services;
using Xunit;

namespace SteamDb.Tests;

public class GameLibraryServiceTests
{
    private const string Key = "key";
    private const string Id = "id";

    [Fact]
    public async Task FetchAsync_NoSteamCredentials_AndNoStoreConnected_Throws()
    {
        var service = new GameLibraryService(new FakeFactory());

        var ex = await Assert.ThrowsAsync<Exception>(() => service.FetchAsync(null, null));
        Assert.Contains("Steam credentials", ex.Message);
    }

    [Fact]
    public async Task FetchAsync_SteamResponseMissingGames_Throws()
    {
        var factory = new FakeFactory { Steam = new FakeSteamClient(new SteamGamesResponse()) };
        var service = new GameLibraryService(factory);

        var ex = await Assert.ThrowsAsync<Exception>(() => service.FetchAsync(Key, Id));
        Assert.Contains("from Steam", ex.Message);
    }

    [Fact]
    public async Task FetchAsync_SteamOnly_ReturnsSteamRows_WithNoStoreFlags()
    {
        var factory = new FakeFactory { Steam = SteamWith(("Portal", 400)) };
        var service = new GameLibraryService(factory);

        var result = await service.FetchAsync(Key, Id);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Portal", row.Name);
        Assert.Equal(400, row.SteamGameId);
        Assert.False(result.EpicAuthenticated);
        Assert.False(result.GogAuthenticated);
        Assert.False(result.XboxAuthenticated);
        Assert.False(result.EpicSessionExpired);
    }

    [Fact]
    public async Task FetchAsync_AllSourcesAuthenticated_MergesEveryLibrary()
    {
        var factory = new FakeFactory
        {
            Steam = SteamWith(("Steam Game", 1)),
            Epic = new FakeEpicClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new EpicGame { Title = "Epic Game", CatalogItemId = "cat", Namespace = "ns" } }
            },
            Gog = new FakeGogClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new GogGame { Title = "Gog Game", Id = 5 } }
            },
            Xbox = new FakeXboxClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new XboxGame { Name = "Xbox Game", TitleId = "9X", IsGamePass = true } }
            }
        };
        var service = new GameLibraryService(factory);

        var result = await service.FetchAsync(Key, Id);

        Assert.True(result.EpicAuthenticated);
        Assert.True(result.GogAuthenticated);
        Assert.True(result.XboxAuthenticated);
        Assert.Equal(4, result.Rows.Count);
        Assert.True(result.Rows.Single(r => r.Name == "Xbox Game").IsGamePass);

        // Authenticated stores were actually queried.
        Assert.True(((FakeEpicClient)factory.Epic).GamesFetched);
        Assert.True(((FakeGogClient)factory.Gog).GamesFetched);
        Assert.True(((FakeXboxClient)factory.Xbox).GamesFetched);
    }

    [Fact]
    public async Task FetchAsync_SessionExpired_FlagsItAndSkipsThatStoresFetch()
    {
        var epic = new FakeEpicClient(StoreAuthFromCacheStatus.SessionExpired)
        {
            Games = { new EpicGame { Title = "Should Not Appear", CatalogItemId = "c", Namespace = "n" } }
        };
        var factory = new FakeFactory { Steam = SteamWith(("Steam Game", 1)), Epic = epic };
        var service = new GameLibraryService(factory);

        var result = await service.FetchAsync(Key, Id);

        Assert.False(result.EpicAuthenticated);
        Assert.True(result.EpicSessionExpired);
        Assert.False(epic.GamesFetched);                      // expired session is not queried
        Assert.DoesNotContain(result.Rows, r => r.Name == "Should Not Appear");
        Assert.Single(result.Rows);                           // only the Steam game
    }

    [Fact]
    public async Task FetchAsync_OnlyOneStoreConnected_NoSteam_Succeeds()
    {
        var factory = new FakeFactory
        {
            Xbox = new FakeXboxClient(StoreAuthFromCacheStatus.Authenticated)
            {
                Games = { new XboxGame { Name = "Halo", TitleId = "9H" } }
            }
        };
        var service = new GameLibraryService(factory);

        var result = await service.FetchAsync(null, null);

        Assert.True(result.XboxAuthenticated);
        Assert.Single(result.Rows);
        Assert.Equal("Halo", result.Rows[0].Name);
    }

    private static ISteamClient SteamWith(params (string Name, int Id)[] games)
    {
        return new FakeSteamClient(new SteamGamesResponse
        {
            Response = new SteamGamesInnerResponse
            {
                Games = games.Select(g => new SteamGame { Name = g.Name, GameID = g.Id }).ToList()
            }
        });
    }
}

// ---- Fakes -----------------------------------------------------------------------------

file sealed class FakeFactory : IStoreClientFactory
{
    public IEpicClient Epic { get; init; } = new FakeEpicClient(StoreAuthFromCacheStatus.NoCachedSession);
    public IGogClient Gog { get; init; } = new FakeGogClient(StoreAuthFromCacheStatus.NoCachedSession);
    public IXboxClient Xbox { get; init; } = new FakeXboxClient(StoreAuthFromCacheStatus.NoCachedSession);
    public ISteamClient Steam { get; init; } = new FakeSteamClient(new SteamGamesResponse());

    public IEpicClient CreateEpic() => Epic;
    public IGogClient CreateGog() => Gog;
    public IXboxClient CreateXbox() => Xbox;
    public ISteamClient CreateSteam() => Steam;
}

file abstract class FakeStoreClient
{
    public void OpenLoginPageInBrowser() { }
    public Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode) => Task.FromResult(true);
    public Uri LoginRequestUri => new("https://example.test");
    public Uri LoginRedirectUri => new("https://example.test");
}

file sealed class FakeEpicClient(StoreAuthFromCacheStatus status) : FakeStoreClient, IEpicClient
{
    public List<EpicGame> Games { get; init; } = [];
    public bool GamesFetched { get; private set; }

    public Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync() => Task.FromResult(status);

    public Task<List<EpicGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        GamesFetched = true;
        return Task.FromResult(Games);
    }
}

file sealed class FakeGogClient(StoreAuthFromCacheStatus status) : FakeStoreClient, IGogClient
{
    public List<GogGame> Games { get; init; } = [];
    public bool GamesFetched { get; private set; }

    public Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync() => Task.FromResult(status);

    public Task<List<GogGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        GamesFetched = true;
        return Task.FromResult(Games);
    }
}

file sealed class FakeXboxClient(StoreAuthFromCacheStatus status) : FakeStoreClient, IXboxClient
{
    public List<XboxGame> Games { get; init; } = [];
    public bool GamesFetched { get; private set; }

    public Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync() => Task.FromResult(status);

    public Task<List<XboxGame>> GetGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        GamesFetched = true;
        return Task.FromResult(Games);
    }
}

file sealed class FakeSteamClient(SteamGamesResponse response) : ISteamClient
{
    public Task<SteamGamesResponse> GetOwnedGames(string? steamId, string? apiKey, CancellationToken ct = default) =>
        Task.FromResult(response);
}
