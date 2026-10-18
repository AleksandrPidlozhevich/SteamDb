using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamDb.Services;

/// <summary>
/// Which stores the user unticked under "include in export". Shared (DI singleton) between the
/// main window, which edits it, and <see cref="GameLibraryService"/>, which skips those stores'
/// libraries — so every export, the library viewer and <c>--sync</c> honour the same choice.
/// Persisted as a comma-separated list in the remembered settings.
/// </summary>
public sealed class StoreSelection
{
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Excluded => _excluded;

    public bool Includes(string store) => !_excluded.Contains(store);

    public void SetIncluded(string store, bool included)
    {
        if (included) _excluded.Remove(store);
        else _excluded.Add(store);
    }

    public void Load(string? serialized)
    {
        _excluded.Clear();
        foreach (var store in Parse(serialized)) _excluded.Add(store);
    }

    /// <summary>"GOG, Xbox", or null when every store is included.</summary>
    public string? Serialize() =>
        _excluded.Count == 0 ? null : string.Join(", ", _excluded.Order(StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> Parse(string? serialized) =>
        (serialized ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => s.Length > 0);
}
