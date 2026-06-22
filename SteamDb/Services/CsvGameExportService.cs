using SteamDb.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamDb.Services;

public sealed class CsvGameExportRow
{
    public string Name { get; set; } = string.Empty;

    public bool HasSteam { get; set; }

    public bool HasEpic { get; set; }

    public bool HasGog { get; set; }

    public int? SteamGameId { get; set; }

    public string? CatalogItemId { get; set; }

    public string? Namespace { get; set; }

    public long? GogId { get; set; }

    public bool HasXbox { get; set; }

    public string? XboxTitleId { get; set; }

    /// <summary>Xbox title played via Game Pass (played, not owned). Surfaced as a "Game Pass" tag.</summary>
    public bool IsGamePass { get; set; }

    /// <summary>True when the row carries something worth keeping (a name or any identifier).</summary>
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Name) ||
        SteamGameId.HasValue ||
        !string.IsNullOrWhiteSpace(CatalogItemId) ||
        !string.IsNullOrWhiteSpace(Namespace) ||
        GogId.HasValue ||
        !string.IsNullOrWhiteSpace(XboxTitleId);

    /// <summary>Combined identifier cell with platform prefixes, e.g. "Steam:236870; GOG:123".</summary>
    public string IdText => BuildIdField();

    public string ToCsvLine()
    {
        // Three columns only: Platform, Name, ID. Platform and all identifiers are each
        // collapsed into a single cell using non-comma separators so nothing spills into
        // neighbouring columns when opened in Excel.
        return string.Join(",",
            EscapeCsvField(BuildPlatformField()),
            EscapeCsvField(Name),
            EscapeCsvField(BuildIdField()));
    }

    private string BuildPlatformField()
    {
        var parts = new List<string>();
        if (HasSteam) parts.Add("Steam");
        if (HasEpic) parts.Add("Epic");
        if (HasGog) parts.Add("GOG");
        if (HasXbox) parts.Add("Xbox");
        if (IsGamePass) parts.Add("Game Pass");
        return string.Join("/", parts);
    }

    private string BuildIdField()
    {
        var parts = new List<string>();
        if (SteamGameId.HasValue)
            parts.Add($"Steam:{SteamGameId.Value}");
        if (!string.IsNullOrEmpty(Namespace) && !string.IsNullOrEmpty(CatalogItemId))
            parts.Add($"Epic:{Namespace}/{CatalogItemId}");
        if (GogId.HasValue)
            parts.Add($"GOG:{GogId.Value}");
        if (!string.IsNullOrEmpty(XboxTitleId))
            parts.Add($"Xbox:{XboxTitleId}");
        return string.Join("; ", parts);
    }

    public void MergeFrom(CsvGameExportRow other)
    {
        HasSteam |= other.HasSteam;
        HasEpic |= other.HasEpic;
        HasGog |= other.HasGog;
        HasXbox |= other.HasXbox;
        IsGamePass |= other.IsGamePass;

        if (other.SteamGameId.HasValue)
            SteamGameId = other.SteamGameId;

        if (!string.IsNullOrEmpty(other.CatalogItemId))
            CatalogItemId = other.CatalogItemId;

        if (!string.IsNullOrEmpty(other.Namespace))
            Namespace = other.Namespace;

        if (other.GogId.HasValue)
            GogId = other.GogId;

        if (!string.IsNullOrEmpty(other.XboxTitleId))
            XboxTitleId = other.XboxTitleId;

        if (!string.IsNullOrEmpty(other.Name))
            Name = other.Name;
    }

    private static string EscapeCsvField(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n'))
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public static class CsvGameExportService
{
    public const string Header = "Platform,Name,ID";

    /// <summary>
    /// Returns the required columns missing from the file's header row.
    /// An empty list means the file is a valid SteamDb CSV that can be updated.
    /// Accepts both the current "ID" column and the legacy "Game ID" column.
    /// </summary>
    public static IReadOnlyList<string> GetMissingColumns(string? content)
    {
        var headerFields = ReadHeaderFields(content);
        var missing = new List<string>();

        if (!HasColumn(headerFields, "Platform")) missing.Add("Platform");
        if (!HasColumn(headerFields, "Name")) missing.Add("Name");
        if (!HasColumn(headerFields, "ID") && !HasColumn(headerFields, "Game ID")) missing.Add("ID");

        return missing;
    }

    private static List<string> ReadHeaderFields(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var firstLine = content
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine)
            ? []
            : ParseCsvFields(firstLine).Select(field => field.Trim()).ToList();
    }

    private static bool HasColumn(List<string> headerFields, string name)
    {
        return headerFields.Any(header => string.Equals(header, name, StringComparison.OrdinalIgnoreCase));
    }

    public static List<CsvGameExportRow> BuildFromLibraries(
        IEnumerable<SteamGame>? steamGames,
        IEnumerable<EpicGame>? epicGames,
        IEnumerable<GogGame>? gogGames = null,
        IEnumerable<XboxGame>? xboxGames = null)
    {
        var rows = new List<CsvGameExportRow>();
        var indexes = CreateIndexes(rows);

        if (steamGames != null)
            foreach (var game in steamGames)
            {
                var incoming = new CsvGameExportRow
                {
                    Name = game.Name,
                    HasSteam = true,
                    SteamGameId = game.GameID
                };

                AddOrMerge(rows, indexes, incoming);
            }

        if (epicGames != null)
            foreach (var game in epicGames)
            {
                var incoming = new CsvGameExportRow
                {
                    Name = game.Title ?? game.AppName,
                    HasEpic = true,
                    CatalogItemId = game.CatalogItemId,
                    Namespace = game.Namespace
                };

                AddOrMerge(rows, indexes, incoming);
            }

        if (gogGames != null)
            foreach (var game in gogGames)
            {
                var incoming = new CsvGameExportRow
                {
                    Name = game.Title,
                    HasGog = true,
                    GogId = game.Id
                };

                AddOrMerge(rows, indexes, incoming);
            }

        if (xboxGames != null)
            foreach (var game in xboxGames)
            {
                var incoming = new CsvGameExportRow
                {
                    Name = game.Name,
                    HasXbox = true,
                    XboxTitleId = game.TitleId,
                    IsGamePass = game.IsGamePass
                };

                AddOrMerge(rows, indexes, incoming);
            }

        return rows;
    }

    /// <summary>Builds a row from a platform label and a combined "platform:id" field
    /// (used when importing existing rows back from an external target like Notion).</summary>
    public static CsvGameExportRow CreateRow(string? platform, string name, string? idField)
    {
        var row = new CsvGameExportRow { Name = name };
        ApplyIdField(row, idField ?? string.Empty);

        row.HasSteam = PlatformIncludes(platform ?? string.Empty, "Steam") || row.SteamGameId.HasValue;
        row.HasEpic = PlatformIncludes(platform ?? string.Empty, "Epic") ||
                      (!string.IsNullOrEmpty(row.CatalogItemId) && !string.IsNullOrEmpty(row.Namespace));
        row.HasGog = PlatformIncludes(platform ?? string.Empty, "GOG") || row.GogId.HasValue;
        row.HasXbox = PlatformIncludes(platform ?? string.Empty, "Xbox") || !string.IsNullOrEmpty(row.XboxTitleId);
        row.IsGamePass = PlatformIncludes(platform ?? string.Empty, "Game Pass");
        return row;
    }

    public static List<CsvGameExportRow> Parse(string content)
    {
        var rows = new List<CsvGameExportRow>();
        if (string.IsNullOrWhiteSpace(content))
            return rows;

        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var fields = ParseCsvFields(line);

            // Skip the header row of any format (no game's platform is literally "Platform").
            if (fields.Count > 0 && string.Equals(fields[0].Trim(), "Platform", StringComparison.OrdinalIgnoreCase))
                continue;

            var row = ParseLine(fields);
            if (row is { HasContent: true })
                rows.Add(row);
        }

        return rows;
    }

    public static string Serialize(IEnumerable<CsvGameExportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);

        foreach (var row in rows.Where(row => row.HasContent)
                     .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine(row.ToCsvLine());

        return builder.ToString();
    }

    public static (List<CsvGameExportRow> Rows, int Added, int Updated) Merge(
        IEnumerable<CsvGameExportRow> existingRows,
        IEnumerable<CsvGameExportRow> incomingRows)
    {
        var rows = existingRows.Select(CloneRow).ToList();
        var indexes = CreateIndexes(rows);
        var added = 0;
        var updated = 0;

        foreach (var incoming in incomingRows)
        {
            var match = FindMatch(indexes, incoming);
            if (match == null)
            {
                rows.Add(CloneRow(incoming));
                RegisterRow(indexes, rows[^1]);
                added++;
                continue;
            }

            match.MergeFrom(incoming);
            RegisterRow(indexes, match);
            updated++;
        }

        return (rows, added, updated);
    }

    private static CsvGameExportRow? ParseLine(List<string> fields)
    {
        if (fields.Count == 0)
            return null;

        // Legacy wide format: Platform, Name, Game ID, Catalog Item Id, Namespace.
        if (fields.Count >= 5)
        {
            var platform = fields[0];
            var gameId = ParseInt(fields[^3]);
            var catalogItemId = NullIfEmpty(fields[^2]);
            var ns = NullIfEmpty(fields[^1]);
            var name = string.Join(",", fields.Skip(1).Take(fields.Count - 4));

            return new CsvGameExportRow
            {
                Name = name,
                HasSteam = PlatformIncludes(platform, "Steam") || gameId.HasValue,
                HasEpic = PlatformIncludes(platform, "Epic") ||
                          !string.IsNullOrEmpty(catalogItemId) ||
                          !string.IsNullOrEmpty(ns),
                SteamGameId = gameId,
                CatalogItemId = catalogItemId,
                Namespace = ns
            };
        }

        // Current format: Platform, Name, ID.
        if (fields.Count >= 3)
        {
            var platform = fields[0];
            var row = new CsvGameExportRow { Name = fields[1] };
            ApplyIdField(row, fields[2]);

            row.HasSteam = PlatformIncludes(platform, "Steam") || row.SteamGameId.HasValue;
            row.HasEpic = PlatformIncludes(platform, "Epic") ||
                          (!string.IsNullOrEmpty(row.CatalogItemId) && !string.IsNullOrEmpty(row.Namespace));
            row.HasGog = PlatformIncludes(platform, "GOG") || row.GogId.HasValue;
            row.HasXbox = PlatformIncludes(platform, "Xbox") || !string.IsNullOrEmpty(row.XboxTitleId);
            row.IsGamePass = PlatformIncludes(platform, "Game Pass");
            return row;
        }

        // Oldest format: Name, Game ID.
        if (fields.Count == 2)
            return new CsvGameExportRow
            {
                Name = fields[0],
                HasSteam = true,
                SteamGameId = ParseInt(fields[1])
            };

        return null;
    }

    // Parses the combined ID cell, e.g. "Steam:236870; Epic:{namespace}/{catalogItemId}".
    private static void ApplyIdField(CsvGameExportRow row, string idField)
    {
        if (string.IsNullOrWhiteSpace(idField))
            return;

        foreach (var part in idField.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith("Steam:", StringComparison.OrdinalIgnoreCase))
            {
                row.SteamGameId = ParseInt(part["Steam:".Length..]);
            }
            else if (part.StartsWith("Epic:", StringComparison.OrdinalIgnoreCase))
            {
                var epic = part["Epic:".Length..];
                var slash = epic.IndexOf('/');
                if (slash > 0 && slash < epic.Length - 1)
                {
                    row.Namespace = NullIfEmpty(epic[..slash]);
                    row.CatalogItemId = NullIfEmpty(epic[(slash + 1)..]);
                }
            }
            else if (part.StartsWith("GOG:", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(part["GOG:".Length..].Trim(), out var gogId))
                    row.GogId = gogId;
            }
            else if (part.StartsWith("Xbox:", StringComparison.OrdinalIgnoreCase))
            {
                row.XboxTitleId = NullIfEmpty(part["Xbox:".Length..]);
            }
    }

    private static List<string> ParseCsvFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static void AddOrMerge(
        List<CsvGameExportRow> rows,
        CsvIndexes indexes,
        CsvGameExportRow incoming)
    {
        var match = FindMatch(indexes, incoming);
        if (match == null)
        {
            rows.Add(incoming);
            RegisterRow(indexes, incoming);
            return;
        }

        match.MergeFrom(incoming);
        RegisterRow(indexes, match);
    }

    private static CsvGameExportRow? FindMatch(CsvIndexes indexes, CsvGameExportRow incoming)
    {
        if (incoming.SteamGameId is int steamId &&
            indexes.BySteamId.TryGetValue(steamId, out var bySteam))
            return bySteam;

        var epicKey = BuildEpicKey(incoming.Namespace, incoming.CatalogItemId);
        if (epicKey != null && indexes.ByEpicKey.TryGetValue(epicKey, out var byEpic))
            return byEpic;

        if (incoming.GogId is long gogId &&
            indexes.ByGogId.TryGetValue(gogId, out var byGog))
            return byGog;

        if (!string.IsNullOrEmpty(incoming.XboxTitleId) &&
            indexes.ByXboxId.TryGetValue(incoming.XboxTitleId, out var byXbox))
            return byXbox;

        var nameKey = NormalizeGameName(incoming.Name);
        if (!string.IsNullOrEmpty(nameKey) &&
            indexes.ByName.TryGetValue(nameKey, out var byName))
            return byName;

        return null;
    }

    private static CsvIndexes CreateIndexes(IEnumerable<CsvGameExportRow> rows)
    {
        var indexes = new CsvIndexes();
        foreach (var row in rows)
            RegisterRow(indexes, row);

        return indexes;
    }

    private static void RegisterRow(CsvIndexes indexes, CsvGameExportRow row)
    {
        if (row.SteamGameId is int steamId)
            indexes.BySteamId[steamId] = row;

        var epicKey = BuildEpicKey(row.Namespace, row.CatalogItemId);
        if (epicKey != null)
            indexes.ByEpicKey[epicKey] = row;

        if (row.GogId is long gogId)
            indexes.ByGogId[gogId] = row;

        if (!string.IsNullOrEmpty(row.XboxTitleId))
            indexes.ByXboxId[row.XboxTitleId] = row;

        var nameKey = NormalizeGameName(row.Name);
        if (!string.IsNullOrEmpty(nameKey))
            indexes.ByName[nameKey] = row;
    }

    private static CsvGameExportRow CloneRow(CsvGameExportRow row)
    {
        return new CsvGameExportRow
        {
            Name = row.Name,
            HasSteam = row.HasSteam,
            HasEpic = row.HasEpic,
            HasGog = row.HasGog,
            HasXbox = row.HasXbox,
            IsGamePass = row.IsGamePass,
            SteamGameId = row.SteamGameId,
            CatalogItemId = row.CatalogItemId,
            Namespace = row.Namespace,
            GogId = row.GogId,
            XboxTitleId = row.XboxTitleId
        };
    }

    private static string? BuildEpicKey(string? ns, string? catalogItemId)
    {
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(catalogItemId))
            return null;

        return $"{ns.Trim().ToLowerInvariant()}|{catalogItemId.Trim().ToLowerInvariant()}";
    }

    private static int? ParseInt(string value)
    {
        return int.TryParse(value.Trim(), out var parsed) ? parsed : null;
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool PlatformIncludes(string platform, string token)
    {
        return platform.Split(['/', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeGameName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[™®©]", "");
        normalized = Regex.Replace(normalized, @"[^\w\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    /// <summary>Reusable matcher over a set of rows using the same dedup keys as
    /// <see cref="Merge"/>: Steam id, Epic namespace/catalog id, GOG id, then normalized name.</summary>
    public sealed class RowMatcher
    {
        private readonly CsvIndexes _indexes;

        public RowMatcher(IEnumerable<CsvGameExportRow> rows)
        {
            _indexes = CreateIndexes(rows);
        }

        public CsvGameExportRow? Match(CsvGameExportRow incoming)
        {
            return FindMatch(_indexes, incoming);
        }

        public void Register(CsvGameExportRow row)
        {
            RegisterRow(_indexes, row);
        }
    }

    private sealed class CsvIndexes
    {
        public Dictionary<int, CsvGameExportRow> BySteamId { get; } = new();

        public Dictionary<string, CsvGameExportRow> ByEpicKey { get; } = new(StringComparer.Ordinal);

        public Dictionary<long, CsvGameExportRow> ByGogId { get; } = new();

        public Dictionary<string, CsvGameExportRow> ByXboxId { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CsvGameExportRow> ByName { get; } = new(StringComparer.Ordinal);
    }
}