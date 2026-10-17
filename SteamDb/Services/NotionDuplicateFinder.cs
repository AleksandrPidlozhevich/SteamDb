using SteamDb.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamDb.Services;

/// <summary>
/// Pages of the same game found by <see cref="NotionDuplicateFinder"/>: the one to keep (the oldest,
/// most likely the one with the user's notes / status) and the ones to move to Notion's trash.
/// </summary>
public sealed record NotionDuplicateGroup(NotionGameRow Keep, IReadOnlyList<NotionGameRow> Remove, CsvGameExportRow Merged)
{
    public string Name => Keep.Row.Name;

    /// <summary>"Steam/GOG · Steam:1; GOG:2" — what the kept page will hold after the merge.</summary>
    public string ResultText => Join(Merged.PlatformText, Merged.IdText);

    /// <summary>"Witcher 3 (GOG · GOG:2)" for each page that goes to the trash.</summary>
    public string RemoveText => string.Join(", ", Remove.Select(r =>
        $"{r.Row.Name} ({Join(string.Join("/", r.Platforms), r.GameId)})"));

    private static string Join(string platforms, string id) =>
        string.Join(" · ", new[] { platforms, id }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Finds pages in the Notion database that are the same game (e.g. added by hand before the first
/// export, or by an older build). Two pages match when they share a store id, or have the same
/// normalized name and no conflicting id for the same store (so "Doom" (1993) and "Doom" (2016),
/// with different Steam ids, stay apart). Pure, so it's unit-tested.
/// </summary>
public static class NotionDuplicateFinder
{
    public static IReadOnlyList<NotionDuplicateGroup> Find(IReadOnlyList<NotionGameRow> pages)
    {
        // Oldest first, so the kept page is the original one; pages without a date keep their order.
        var ordered = pages
            .Select((page, index) => (page, index))
            .OrderBy(x => x.page.CreatedTime ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.page);

        var groups = new List<Group>();
        var byId = new Dictionary<(string Platform, string Key), Group>();
        var byName = new Dictionary<string, List<Group>>(StringComparer.Ordinal);

        foreach (var page in ordered)
        {
            var row = page.Row;
            Group? target = null;

            foreach (var d in PlatformCatalog.All)
                if (d.MatchKey(row) is { } key && byId.TryGetValue((d.Label, key), out var byKey))
                {
                    target = byKey;
                    break;
                }

            var name = CsvGameExportService.NormalizeGameName(row.Name);
            if (target == null && name.Length > 0 && byName.TryGetValue(name, out var sameName))
                target = sameName.FirstOrDefault(g => !Conflicts(g.Merged, row));

            // Same id for one store but a different id for another: not safe to merge automatically.
            if (target != null && Conflicts(target.Merged, row)) target = null;

            if (target == null)
            {
                target = new Group(CsvGameExportService.CreateRow(row.PlatformText, row.Name, row.IdText));
                groups.Add(target);
            }
            else
            {
                var keptName = target.Merged.Name;
                target.Merged.MergeFrom(row);
                target.Merged.Name = keptName; // the kept page keeps its title
            }

            target.Pages.Add(page);

            foreach (var d in PlatformCatalog.All)
                if (d.MatchKey(target.Merged) is { } key)
                    byId[(d.Label, key)] = target;
            if (name.Length > 0)
            {
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
                if (!list.Contains(target)) list.Add(target);
            }
        }

        return groups
            .Where(g => g.Pages.Count > 1)
            .Select(g => new NotionDuplicateGroup(g.Pages[0], g.Pages.Skip(1).ToList(), g.Merged))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool Conflicts(CsvGameExportRow a, CsvGameExportRow b) =>
        PlatformCatalog.All.Any(d => d.MatchKey(a) is { } ka && d.MatchKey(b) is { } kb && ka != kb);

    private sealed class Group(CsvGameExportRow merged)
    {
        public CsvGameExportRow Merged { get; } = merged;
        public List<NotionGameRow> Pages { get; } = [];
    }
}
