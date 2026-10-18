using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilewise.Services;

/// <summary>
/// Games the user hid in the library viewer (demos, test servers, tools…). Hidden games are left out
/// of every export, but a page / row already in a target is left alone (never "no longer owned").
/// A game is remembered by its store ids (<c>Steam:400</c>, <c>GOG:123</c>…), or by its name when it
/// has none, so it stays hidden whichever store lists it. Kept in the encrypted secret store
/// (key <c>ignored-games</c>, one key per line); a DI singleton shared by the app and <c>--sync</c>.
/// </summary>
public sealed class IgnoredGames
{
    private const string SecretKey = "ignored-games";

    private readonly ISecretStore? _secrets;
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public IgnoredGames(ISecretStore? secrets = null)
    {
        _secrets = secrets;
        var stored = secrets?.Load(SecretKey);
        if (string.IsNullOrEmpty(stored)) return;
        foreach (var key in stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _keys.Add(key);
    }

    public int Count => _keys.Count;

    public bool IsIgnored(CsvGameExportRow row) => KeysOf(row).Any(_keys.Contains);

    public void Ignore(CsvGameExportRow row)
    {
        foreach (var key in KeysOf(row)) _keys.Add(key);
        Save();
    }

    public void Unignore(CsvGameExportRow row)
    {
        foreach (var key in KeysOf(row)) _keys.Remove(key);
        Save();
    }

    /// <summary>The keys a row is remembered by: each store id, or the normalized name without ids.</summary>
    public static IReadOnlyList<string> KeysOf(CsvGameExportRow row)
    {
        var keys = new List<string>();
        foreach (var d in PlatformCatalog.All)
            if (d.FormatId(row) is { } id)
                keys.Add($"{d.Label}:{id}");

        if (keys.Count == 0)
        {
            var name = CsvGameExportService.NormalizeGameName(row.Name);
            if (name.Length > 0) keys.Add($"Name:{name}");
        }

        return keys;
    }

    /// <summary>Splits a fetched library into the rows to export and the hidden ones.</summary>
    public (List<CsvGameExportRow> Kept, List<CsvGameExportRow> Ignored) Split(IEnumerable<CsvGameExportRow> rows)
    {
        var kept = new List<CsvGameExportRow>();
        var ignored = new List<CsvGameExportRow>();
        foreach (var row in rows) (IsIgnored(row) ? ignored : kept).Add(row);
        return (kept, ignored);
    }

    private void Save() =>
        _secrets?.Save(SecretKey, string.Join("\n", _keys.Order(StringComparer.OrdinalIgnoreCase)));
}
