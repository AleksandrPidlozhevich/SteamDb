using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SteamDb.Services;

/// <summary>0-based column indexes of the SteamDb columns, after any migration has been applied.
/// <see cref="Playtime"/> / <see cref="LastPlayed"/> are opt-in (the user adds the header); -1 = absent.</summary>
public sealed record SheetColumns(
    int Platform, int Name, int Id, int Completed, int Playtime = -1, int LastPlayed = -1, int Owned = -1)
{
    /// <summary>Number of columns needed to hold every SteamDb column.</summary>
    public int Width => new[] { Platform, Name, Id, Completed, Playtime, LastPlayed, Owned }.Max() + 1;
}

/// <summary>A header cell to write (row 0) — only the SteamDb columns the sheet is missing.</summary>
public sealed record SheetHeaderCell(int Column, string Text);

/// <summary>New values for an existing row (0-based index in the migrated sheet). Playtime /
/// LastPlayed are null when there's nothing to write (column absent or no fresh data).</summary>
public sealed record SheetRowUpdate(
    int RowIndex, string Platform, string Id, double? PlaytimeHours = null, string? LastPlayed = null,
    bool? Owned = null);

/// <summary>
/// Everything the Google Sheets export has to write, computed from the sheet's current values.
/// Structural changes (<see cref="InsertPlatformColumn"/>, <see cref="InsertHeaderRow"/>) are applied
/// first; every index in the plan already accounts for them.
/// </summary>
public sealed record GoogleSheetsSyncPlan(
    bool InsertPlatformColumn,
    bool InsertHeaderRow,
    SheetColumns Columns,
    IReadOnlyList<SheetHeaderCell> HeaderCells,
    IReadOnlyList<SheetRowUpdate> Updates,
    IReadOnlyList<CsvGameExportRow> NewRows,
    int FirstNewRowIndex,
    IReadOnlyList<string> NoLongerOwned)
{
    /// <summary>Rows the sheet holds once the plan is applied (header included).</summary>
    public int TotalRows => FirstNewRowIndex + NewRows.Count;
}

/// <summary>
/// Reconciles the fetched library with an existing Google Sheet, Notion-style: existing rows are
/// updated in place (so the user's Completed ticks, extra columns and ordering survive) and new
/// games are appended. Pure logic over the sheet's cell values, so it's unit-tested without Google.
/// </summary>
/// <remarks>
/// Layout: <c>Platform | Name | ID | Completed</c>, the same cells as the CSV export. Columns are
/// found by header text, so the user may reorder them. Sheets from older builds are migrated:
/// <c>Name | Game ID | Completed</c> (Steam-only, bare ids) gets a Platform column inserted at A,
/// and the headerless variant (an old bug never wrote the header) also gets a header row.
/// </remarks>
public static class GoogleSheetsSyncPlanner
{
    public const string PlatformHeader = "Platform";
    public const string NameHeader = "Name";
    public const string IdHeader = "ID";
    public const string CompletedHeader = "Completed";
    public const string PlaytimeHeader = "Playtime";
    public const string LastPlayedHeader = "Last played";
    public const string OwnedHeader = "Owned";

    private static readonly string[] IdHeaders = [IdHeader, "Game ID", "GameID"];

    /// <param name="fetchedStores">Stores whose library was loaded; a sheet row missing from the fetch
    /// is "no longer owned" only when all of its stores are in here (see <c>Ownership</c>).</param>
    public static GoogleSheetsSyncPlan Plan(
        IList<IList<object>>? values, IEnumerable<CsvGameExportRow> incoming,
        IReadOnlySet<string>? fetchedStores = null, IEnumerable<CsvGameExportRow>? ignored = null)
    {
        fetchedStores ??= new HashSet<string>();
        values ??= [];
        var layout = DetectLayout(values);
        var rowShift = layout.InsertHeaderRow ? 1 : 0;

        var existing = ReadExistingRows(values, layout, rowShift);
        var matcher = new CsvGameExportService.RowMatcher(existing.Select(e => e.Row));
        var newRows = new List<CsvGameExportRow>();
        var matched = new HashSet<CsvGameExportRow>(ReferenceEqualityComparer.Instance);

        // Merge each owned game into its existing row, or queue a brand-new row.
        foreach (var row in incoming)
        {
            var match = matcher.Match(row);
            if (match == null)
            {
                newRows.Add(row);
                matcher.Register(row);
                continue;
            }

            match.MergeFrom(row);
            matched.Add(match);
        }

        // Hidden games: their rows are left as they are, but they're still owned.
        foreach (var row in ignored ?? [])
            if (matcher.Match(row) is { } hidden)
                matched.Add(hidden);

        // Rewrite any row whose stored cells no longer match — merged-in platforms, legacy bare ids /
        // missing platforms normalised to the current form, and fresh playtime / last-played values
        // (opt-in columns; only written when the fetch reported a value for that game).
        var columns = layout.Columns;
        var updates = new List<SheetRowUpdate>();
        var noLongerOwned = new List<string>();
        foreach (var e in existing)
        {
            var ownership = CsvGameExportService.Ownership(e.Row, matched.Contains(e.Row), fetchedStores);
            if (ownership == false) noLongerOwned.Add(e.Row.Name);

            var hours = columns.Playtime >= 0 ? e.Row.PlaytimeHours : null;
            var lastPlayed = columns.LastPlayed >= 0 ? e.Row.LastPlayedDate : null;
            var owned = columns.Owned >= 0 ? ownership : null;
            var changed = e.Row.PlatformText != e.StoredPlatform || e.Row.IdText != e.StoredId ||
                          (hours != null && hours != e.StoredPlaytime) ||
                          (lastPlayed != null && lastPlayed != e.StoredLastPlayed) ||
                          (owned != null && owned != e.StoredOwned);
            if (changed)
                updates.Add(new SheetRowUpdate(
                    e.RowIndex, e.Row.PlatformText, e.Row.IdText, hours, lastPlayed, owned));
        }

        return new GoogleSheetsSyncPlan(
            layout.InsertPlatformColumn,
            layout.InsertHeaderRow,
            layout.Columns,
            layout.HeaderCells,
            updates,
            newRows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            // Row 0 is always the header (written now if the sheet was empty).
            Math.Max(values.Count + rowShift, 1),
            noLongerOwned.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    // Where the SteamDb columns are in the sheet as read (Orig*, -1 = absent) and where they end up.
    private sealed record Layout(
        bool InsertPlatformColumn,
        bool InsertHeaderRow,
        int OrigPlatform,
        int OrigName,
        int OrigId,
        int FirstDataRow,
        SheetColumns Columns,
        IReadOnlyList<SheetHeaderCell> HeaderCells,
        int OrigPlaytime = -1,
        int OrigLastPlayed = -1,
        int OrigOwned = -1);

    private static Layout DetectLayout(IList<IList<object>> values)
    {
        var fresh = new SheetColumns(0, 1, 2, 3);

        // Empty sheet: write the full header.
        if (values.Count == 0)
            return new Layout(false, false, -1, -1, -1, 1, fresh, FullHeader(fresh));

        var header = values[0];
        var name = FindColumn(header, NameHeader);
        var id = FindColumn(header, IdHeaders);

        if (name < 0 || id < 0)
        {
            // Headerless sheet from the old exporter: Name, bare Steam id, Completed from row 0.
            if (header.Count >= 2 && long.TryParse(Cell(header, 1), out _))
                return new Layout(true, true, -1, 0, 1, 0, fresh, FullHeader(fresh));

            throw new InvalidOperationException(
                "This spreadsheet doesn't look like a SteamDb sheet: the first row of its first tab " +
                $"must be a header with \"{NameHeader}\" and \"{IdHeader}\" columns. " +
                "Enter a new table name to create a fresh sheet.");
        }

        var platform = FindColumn(header, PlatformHeader);
        var completed = FindColumn(header, CompletedHeader);
        var playtime = FindColumn(header, PlaytimeHeader);
        var lastPlayed = FindColumn(header, LastPlayedHeader);
        var owned = FindColumn(header, OwnedHeader);
        var insertPlatform = platform < 0;
        var shift = insertPlatform ? 1 : 0;

        var columns = new SheetColumns(
            insertPlatform ? 0 : platform,
            name + shift,
            id + shift,
            // A missing Completed column goes right after the last header cell.
            completed >= 0 ? completed + shift : header.Count + shift,
            playtime >= 0 ? playtime + shift : -1,
            lastPlayed >= 0 ? lastPlayed + shift : -1,
            owned >= 0 ? owned + shift : -1);

        var headerCells = new List<SheetHeaderCell>();
        if (insertPlatform) headerCells.Add(new SheetHeaderCell(columns.Platform, PlatformHeader));
        if (completed < 0) headerCells.Add(new SheetHeaderCell(columns.Completed, CompletedHeader));

        return new Layout(insertPlatform, false, platform, name, id, 1, columns, headerCells, playtime, lastPlayed, owned);
    }

    private static List<SheetHeaderCell> FullHeader(SheetColumns c) =>
    [
        new(c.Platform, PlatformHeader),
        new(c.Name, NameHeader),
        new(c.Id, IdHeader),
        new(c.Completed, CompletedHeader)
    ];

    private sealed record ExistingRow(
        int RowIndex, CsvGameExportRow Row, string StoredPlatform, string StoredId,
        double? StoredPlaytime, string StoredLastPlayed, bool? StoredOwned);

    private static List<ExistingRow> ReadExistingRows(IList<IList<object>> values, Layout layout, int rowShift)
    {
        var rows = new List<ExistingRow>();
        for (var r = layout.FirstDataRow; r < values.Count; r++)
        {
            var cells = values[r];
            var platform = layout.OrigPlatform >= 0 ? Cell(cells, layout.OrigPlatform) : string.Empty;
            var name = Cell(cells, layout.OrigName);
            var id = Cell(cells, layout.OrigId);

            var row = CsvGameExportService.CreateRow(platform, name, id);
            CsvGameExportService.ApplyLegacyBareId(row, id);
            if (!row.HasContent) continue;

            rows.Add(new ExistingRow(r + rowShift, row, platform, id,
                Number(cells, layout.OrigPlaytime), Cell(cells, layout.OrigLastPlayed),
                layout.OrigOwned >= 0 && layout.OrigOwned < cells.Count ? cells[layout.OrigOwned] as bool? : null));
        }

        return rows;
    }

    private static int FindColumn(IList<object> header, params string[] names)
    {
        for (var i = 0; i < header.Count; i++)
            if (names.Contains(Cell(header, i), StringComparer.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // A numeric cell, whether it comes back as a number (UNFORMATTED_VALUE) or as text.
    private static double? Number(IList<object> row, int index)
    {
        if (index < 0 || index >= row.Count) return null;
        return row[index] switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => null
        };
    }

    private static string Cell(IList<object> row, int index) =>
        index >= 0 && index < row.Count ? row[index]?.ToString()?.Trim() ?? string.Empty : string.Empty;
}
