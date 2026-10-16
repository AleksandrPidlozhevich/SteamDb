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
    private readonly IGameLibraryService _library;
    private readonly INotionGateway _notion;
    private readonly ILogService _log;

    public NotionGameExporter(IGameLibraryService library, INotionGateway notion, ILogService log)
    {
        _library = library;
        _notion = notion;
        _log = log;
    }

    /// <summary>Plans, then applies, without asking — for callers that skip the preview.</summary>
    public async Task ExportAsync(
        string? steamApiKey, string? steamId, string? notionToken, string? dbId,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var plan = await PlanAsync(steamApiKey, steamId, notionToken, dbId, progress, onStatus, ct);
        await ApplyAsync(notionToken, dbId, plan, progress, onStatus, ct);
    }

    /// <summary>
    /// Dry run: fetches the library and the database and works out which pages would be created or
    /// updated, without writing anything. Pass the result to <see cref="ApplyAsync"/>.
    /// </summary>
    public async Task<NotionExportPlan> PlanAsync(
        string? steamApiKey, string? steamId, string? notionToken, string? dbId,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        // using: the client owns an HttpClient, so it's disposed once the read is done.
        using var notionApiClient = _notion.CreateClient(notionToken, dbId);
        var fetcher = _notion.CreateFetcher(notionApiClient);

        var libraryTask = _library.FetchAsync(steamApiKey, steamId, progress, onStatus, ct);
        var existingTask = fetcher.FetchRowsAsync(ct);
        await Task.WhenAll(libraryTask, existingTask);

        var library = await libraryTask;
        var existing = await existingTask;

        var matcher = new CsvGameExportService.RowMatcher(existing.Select(e => e.Row));

        var toCreate = new List<NotionPageCreate>();

        // Merge each owned game into its existing row, or queue a brand-new page.
        foreach (var row in library.Rows)
        {
            var match = matcher.Match(row);
            if (match == null)
            {
                toCreate.Add(new NotionPageCreate(row.Name, row.PlatformText, BuildProperties(row, true)));
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
            .Select(e => new NotionPageUpdate(
                e.PageId,
                e.Row.Name,
                Describe(string.Join("/", e.Platforms.OrderBy(PlatformOrder)), e.GameId),
                Describe(e.Row.PlatformText, e.Row.IdText),
                BuildProperties(e.Row, false)))
            .ToList();

        return new NotionExportPlan(
            library,
            toCreate.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            toUpdate.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            existing.Count - toUpdate.Count);
    }

    /// <summary>Writes a plan from <see cref="PlanAsync"/> to the database.</summary>
    public async Task ApplyAsync(
        string? notionToken, string? dbId, NotionExportPlan plan,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (plan.IsEmpty)
        {
            _log.WriteInfo("Notion: already up to date.");
            return;
        }

        using var notionApiClient = _notion.CreateClient(notionToken, dbId);

        var total = plan.ToCreate.Count + plan.ToUpdate.Count;
        var done = 0;

        void ReportPage()
        {
            progress?.Report(new StoreFetchProgress(Interlocked.Increment(ref done), total, "Exporting to Notion"));
        }

        onStatus?.Invoke("Exporting to Notion…");
        progress?.Report(new StoreFetchProgress(0, total, "Exporting to Notion"));

        if (plan.ToCreate.Count > 0)
            await notionApiClient.AddPagesToDatabaseParallel(
                plan.ToCreate.Select(c => c.Properties).ToList(), ReportPage, ct);

        if (plan.ToUpdate.Count > 0)
            await notionApiClient.UpdatePagesParallel(
                plan.ToUpdate.Select(u => (u.PageId, u.Properties)).ToList(), ReportPage, ct);

        _log.WriteInfo($"Notion: created {plan.ToCreate.Count}, updated {plan.ToUpdate.Count} games.");
    }

    // "Steam/GOG · Steam:1; GOG:2" — the before/after text shown in the preview.
    private static string Describe(string platforms, string gameId) =>
        string.IsNullOrEmpty(platforms) ? gameId : string.IsNullOrEmpty(gameId) ? platforms : $"{platforms} · {gameId}";

    // Stored tags in catalog order (Steam, Epic, GOG, Xbox, then Game Pass / anything else).
    private static int PlatformOrder(string label)
    {
        var index = Array.FindIndex(PlatformCatalog.All, d => d.Label == label);
        return index < 0 ? int.MaxValue : index;
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

/// <summary>A page the export would create.</summary>
public sealed record NotionPageCreate(string Name, string Platforms, object Properties);

/// <summary>A page the export would update; <see cref="Before"/>/<see cref="After"/> are display text.</summary>
public sealed record NotionPageUpdate(string PageId, string Name, string Before, string After, object Properties);

/// <summary>Result of <see cref="NotionGameExporter.PlanAsync"/>: what an export would change.</summary>
public sealed record NotionExportPlan(
    GameLibraryResult Library,
    IReadOnlyList<NotionPageCreate> ToCreate,
    IReadOnlyList<NotionPageUpdate> ToUpdate,
    int Unchanged)
{
    public bool IsEmpty => ToCreate.Count == 0 && ToUpdate.Count == 0;
}
