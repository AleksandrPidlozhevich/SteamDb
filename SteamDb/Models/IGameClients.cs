using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Models;

// Per-store "fetch the owned library" seams. Return types differ per store, so each store gets its
// own interface (rather than one generic one); GameLibraryService depends on these so the clients
// can be faked in tests.

public interface IEpicClient : IStoreClient
{
    Task<List<EpicGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default);
}

public interface IGogClient : IStoreClient
{
    Task<List<GogGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default);
}

public interface IXboxClient : IStoreClient
{
    Task<List<XboxGame>> GetGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>Steam has no interactive connect flow — just a key/id-based fetch.</summary>
public interface ISteamClient
{
    Task<SteamGamesResponse> GetOwnedGames(string? steamId, string? apiKey, CancellationToken ct = default);
}
