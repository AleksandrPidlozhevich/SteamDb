using SteamDb.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>
/// Pushes the owned Steam / Epic / GOG library into a Notion database, merging against
/// existing rows the same way the CSV export does: new games are created, and rows that
/// gain a platform/id are updated. Targets the database schema —
/// <c>Name</c> (title), <c>Platform's</c> (multi-select) and <c>GameID</c> (text), where
/// GameID holds the platform-prefixed id(s), e.g. "Steam:236870; GOG:123".
/// </summary>
public sealed class NotionGameExporter
{
    private readonly GameLibraryService _library;
    private readonly ILogService _log;

    public NotionGameExporter(GameLibraryService library, ILogService log)
    {
        _library = library;
        _log = log;
    }

    public async Task ExportAsync(
        string? steamApiKey, string? steamId, string? notionToken, string? dbId,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var notionApiClient = new NotionApiClient(notionToken, dbId);
        var fetcher = new NotionDataFetcher(notionApiClient, _log);

        var libraryTask = _library.FetchAsync(steamApiKey, steamId, progress, onStatus, ct);
        var existingTask = fetcher.FetchRowsAsync(ct);
        await Task.WhenAll(libraryTask, existingTask);

        var incoming = (await libraryTask).Rows;
        var existing = await existingTask;

        var matcher = new CsvGameExportService.RowMatcher(existing.Select(e => e.Row));

        var toCreate = new List<object>();

        // Merge each owned game into its existing row, or queue a brand-new page.
        foreach (var row in incoming)
        {
            var match = matcher.Match(row);
            if (match == null)
            {
                toCreate.Add(BuildPage(dbId, row));
                matcher.Register(row);
                continue;
            }

            match.MergeFrom(row);
        }

        // Update any existing page whose stored value no longer matches the desired one.
        // This covers merged-in platforms AND normalising legacy bare ids to the prefixed
        // form (e.g. "1920490" → "Steam:1920490") — even for games no longer owned, since
        // the row's own Platform's tag already tells us the platform.
        var toUpdate = existing
            .Where(e => NotionSignature(e.Row) != StoredSignature(e))
            .Select(e => (e.PageId, Properties: BuildProperties(e.Row, false)))
            .ToList();

        var total = toCreate.Count + toUpdate.Count;
        var done = 0;

        void ReportPage()
        {
            progress?.Report(new StoreFetchProgress(Interlocked.Increment(ref done), total, "Exporting to Notion"));
        }

        if (total > 0)
        {
            onStatus?.Invoke("Exporting to Notion…");
            progress?.Report(new StoreFetchProgress(0, total, "Exporting to Notion"));
        }

        if (toCreate.Count > 0)
            await notionApiClient.AddPagesToDatabaseParallel(toCreate, ReportPage, ct);

        if (toUpdate.Count > 0)
            await notionApiClient.UpdatePagesParallel(toUpdate, ReportPage, ct);

        _log.WriteInfo($"Notion: created {toCreate.Count}, updated {toUpdate.Count} games.");
    }

    // Desired Notion state for a row (tags + platform-prefixed id).
    private static string NotionSignature(CsvGameExportRow row)
    {
        var tags = string.Concat(PlatformCatalog.All.Select(d => d.Has(row)));
        return $"{tags}{row.IsGamePass}|{row.IdText}";
    }

    // What is currently stored on the Notion page (raw tags + raw GameID text), in the same
    // shape as NotionSignature so the two can be compared to decide whether an update is needed.
    private static string StoredSignature(NotionGameRow info)
    {
        var tags = string.Concat(PlatformCatalog.All.Select(d => info.Platforms.Contains(d.Label)));
        return $"{tags}{info.Platforms.Contains(Platforms.GamePass)}|{info.GameId}";
    }

    private static object BuildPage(string? dbId, CsvGameExportRow row)
    {
        return new
        {
            parent = new { database_id = dbId },
            properties = BuildProperties(row, true)
        };
    }

    private static object BuildProperties(CsvGameExportRow row, bool includeName)
    {
        var platforms = new List<object>();
        foreach (var d in PlatformCatalog.All)
            if (d.Has(row))
                platforms.Add(new { name = d.Label });
        if (row.IsGamePass) platforms.Add(new { name = Platforms.GamePass });

        var properties = new Dictionary<string, object>
        {
            [NotionDataFetcher.PlatformProperty] = new { multi_select = platforms },
            // Text column holding platform-prefixed id(s), e.g. "Steam:236870; GOG:123".
            [NotionDataFetcher.GameIdProperty] =
                new { rich_text = new[] { new { text = new { content = row.IdText } } } }
        };

        if (includeName)
            properties[NotionDataFetcher.NameProperty] =
                new { title = new[] { new { text = new { content = row.Name } } } };

        return properties;
    }
}