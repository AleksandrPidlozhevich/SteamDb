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

    /// <summary>The data source's properties as name → Notion type (e.g. "Name" → "title").</summary>
    Task<IReadOnlyDictionary<string, string>> GetPropertyTypesAsync(CancellationToken ct = default);

    /// <summary>Adds empty properties (name → Notion type) to the data source's schema.</summary>
    Task AddPropertiesAsync(IReadOnlyDictionary<string, string> properties, CancellationToken ct = default);

    // The client wraps each body with the data-source parent.
    Task AddPagesToDatabaseParallel(
        IEnumerable<NotionPageBody> pages, Action? onPageDone = null, CancellationToken ct = default);

    Task UpdatePagesParallel(
        IEnumerable<(string PageId, NotionPageBody Body)> updates,
        Action? onPageDone = null,
        CancellationToken ct = default);

    /// <summary>Moves pages to Notion's trash (restorable there for 30 days).</summary>
    Task TrashPagesParallel(IEnumerable<string> pageIds, Action? onPageDone = null, CancellationToken ct = default);
}

/// <summary>A page write: its <c>properties</c> object, plus an external cover image to set (or null).</summary>
public sealed record NotionPageBody(object Properties, string? CoverUrl = null)
{
    /// <summary>The JSON body fields (without the parent).</summary>
    public Dictionary<string, object> ToJson()
    {
        var json = new Dictionary<string, object> { ["properties"] = Properties };
        if (CoverUrl != null)
            json["cover"] = new { type = "external", external = new { url = CoverUrl } };
        return json;
    }
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
