using Newtonsoft.Json.Linq;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Models;

/// <summary>The Notion REST calls the export pipeline needs. Disposable — it owns an HttpClient.</summary>
public interface INotionApiClient : IDisposable
{
    Task<List<JObject>> QueryAllPagesAsync(CancellationToken ct = default);

    Task AddPagesToDatabaseParallel(
        IEnumerable<object> pages, Action? onPageDone = null, CancellationToken ct = default);

    Task UpdatePagesParallel(
        IEnumerable<(string PageId, object Properties)> updates,
        Action? onPageDone = null,
        CancellationToken ct = default);
}

/// <summary>Reads existing Notion database rows, flattened into <see cref="NotionGameRow"/>s.</summary>
public interface INotionDataFetcher
{
    Task<List<NotionGameRow>> FetchRowsAsync(CancellationToken ct = default);
}

/// <summary>
/// Builds Notion clients for a given token/database. Notion clients are per-export (token and
/// database id are runtime values), so they come from a factory rather than the DI container.
/// </summary>
public interface INotionGateway
{
    INotionApiClient CreateClient(string? token, string? dbId);

    INotionDataFetcher CreateFetcher(INotionApiClient client);
}

public sealed class NotionGateway : INotionGateway
{
    private readonly ILogService _log;

    public NotionGateway(ILogService log)
    {
        _log = log;
    }

    public INotionApiClient CreateClient(string? token, string? dbId) => new NotionApiClient(token, dbId);

    public INotionDataFetcher CreateFetcher(INotionApiClient client) => new NotionDataFetcher(client, _log);
}
