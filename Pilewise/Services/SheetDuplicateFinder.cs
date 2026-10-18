using System.Collections.Generic;
using System.Linq;

namespace Pilewise.Services;

/// <summary>
/// Rows of the same game in a Google Sheet: the top-most row is kept and gets every store tag and id
/// (and the Completed tick, if any copy had it); the other rows are deleted.
/// </summary>
public sealed record SheetDuplicateGroup(SheetRow Keep, IReadOnlyList<SheetRow> Remove, CsvGameExportRow Merged)
    : IDuplicateGroupView
{
    public string Name => Keep.Row.Name;

    /// <summary>The kept row should be ticked: it isn't, but a removed copy was.</summary>
    public bool CarriesCompleted => Keep.Completed == false && Remove.Any(r => r.Completed == true);

    public string ResultText => $"row {Keep.RowIndex + 1}: " +
                                DuplicateFinder.Describe(Merged.PlatformText, Merged.IdText) +
                                (CarriesCompleted ? " · Completed ✓" : "");

    /// <summary>"row 40: Witcher 3 (GOG · GOG:2)" for each row that's deleted.</summary>
    public string RemoveText => string.Join(", ", Remove.Select(r =>
        $"row {r.RowIndex + 1}: {r.Row.Name} ({DuplicateFinder.Describe(r.StoredPlatform, r.StoredId)})"));
}

/// <summary>Duplicate rows in a Pilewise Google Sheet (see <see cref="DuplicateFinder"/>). Pure.</summary>
public static class SheetDuplicateFinder
{
    public static IReadOnlyList<SheetDuplicateGroup> Find(IReadOnlyList<SheetRow> rows) =>
        DuplicateFinder.Find(rows.OrderBy(r => r.RowIndex), r => r.Row)
            .Select(m => new SheetDuplicateGroup(m.Keep, m.Remove, m.Merged))
            .ToList();

    /// <summary>
    /// True when every row the groups refer to is still at the same position with the same name and
    /// ids — so a merge (which writes and deletes rows by position) can't hit a row that moved since
    /// the search.
    /// </summary>
    public static bool StillMatches(SheetContents now, IReadOnlyList<SheetDuplicateGroup> groups)
    {
        var byIndex = now.Rows.ToDictionary(r => r.RowIndex);
        return groups.SelectMany(g => g.Remove.Prepend(g.Keep)).All(expected =>
            byIndex.TryGetValue(expected.RowIndex, out var actual) &&
            actual.Row.Name == expected.Row.Name &&
            actual.StoredId == expected.StoredId &&
            actual.StoredPlatform == expected.StoredPlatform);
    }
}
