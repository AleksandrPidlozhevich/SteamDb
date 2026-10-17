using CommunityToolkit.Mvvm.ComponentModel;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamDb.ViewModels;

/// <summary>One game in the library viewer: the merged row flattened for display.</summary>
public sealed record LibraryGameItem(
    string Name,
    IReadOnlyList<string> Platforms,
    string Id,
    int? PlaytimeMinutes,
    DateTimeOffset? LastPlayed)
{
    public static LibraryGameItem From(CsvGameExportRow row)
    {
        var platforms = PlatformCatalog.All.Where(d => d.Has(row)).Select(d => d.Label).ToList();
        if (row.IsGamePass) platforms.Add(Services.Platforms.GamePass);
        return new LibraryGameItem(row.Name, platforms, row.IdText, row.PlaytimeMinutes, row.LastPlayed);
    }

    /// <summary>Owned on two or more stores (Game Pass is a tag, not a purchase).</summary>
    public bool IsMultiStore => Platforms.Count(p => p != Services.Platforms.GamePass) > 1;

    /// <summary>Known to be untouched: a store reported 0 minutes and no store reports a session.</summary>
    public bool IsNeverPlayed => PlaytimeMinutes == 0 && LastPlayed == null;

    /// <summary>"12.5 h", "&lt; 0.1 h", "—" when no store reports playtime.</summary>
    public string PlaytimeText => PlaytimeMinutes switch
    {
        null => "—",
        0 => "0 h",
        < 6 => "< 0.1 h",
        var m => $"{m / 60.0:0.#} h"
    };

    /// <summary>Local date of the last session, or "—".</summary>
    public string LastPlayedText => LastPlayed?.ToLocalTime().ToString("d") ?? "—";
}

/// <summary>A sort choice for the library list.</summary>
public sealed record LibrarySortOption(string Label, LibrarySort Sort)
{
    public override string ToString() => Label;
}

public enum LibrarySort
{
    Name,
    MostPlayed,
    RecentlyPlayed
}

/// <summary>A platform filter choice with its game count, e.g. "GOG (42)".</summary>
public sealed record LibraryFilterOption(string Label, string Key, int Count)
{
    public override string ToString() => $"{Label} ({Count})";
}

/// <summary>
/// The merged library as fetched (before any export): searchable by name, filterable by platform,
/// with per-platform counts. Read-only — nothing here writes to an export target.
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    public const string AllKey = "*";
    public const string MultiStoreKey = "multi";
    public const string NeverPlayedKey = "never";

    private readonly IReadOnlyList<LibraryGameItem> _all;

    [ObservableProperty] private string? searchText;

    [ObservableProperty] private LibraryFilterOption selectedFilter;

    [ObservableProperty] private LibrarySortOption selectedSort;

    [ObservableProperty] private IReadOnlyList<LibraryGameItem> visibleGames = [];

    public LibraryViewModel(IEnumerable<CsvGameExportRow> rows)
    {
        _all = rows
            .Where(r => r.HasContent)
            .Select(LibraryGameItem.From)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var options = new List<LibraryFilterOption> { new("All platforms", AllKey, _all.Count) };
        foreach (var label in PlatformCatalog.All.Select(d => d.Label).Append(Services.Platforms.GamePass))
        {
            var count = _all.Count(g => g.Platforms.Contains(label));
            if (count > 0) options.Add(new LibraryFilterOption(label, label, count));
        }

        var multi = _all.Count(g => g.IsMultiStore);
        if (multi > 0) options.Add(new LibraryFilterOption("Owned on 2+ stores", MultiStoreKey, multi));

        var never = _all.Count(g => g.IsNeverPlayed);
        if (never > 0) options.Add(new LibraryFilterOption("Never played", NeverPlayedKey, never));

        Filters = options;
        selectedFilter = options[0];
        Sorts =
        [
            new LibrarySortOption("Sort: name", LibrarySort.Name),
            new LibrarySortOption("Sort: most played", LibrarySort.MostPlayed),
            new LibrarySortOption("Sort: recently played", LibrarySort.RecentlyPlayed)
        ];
        selectedSort = Sorts[0];
        ApplyFilter();
    }

    /// <summary>Design-time only (Avalonia previewer).</summary>
    public LibraryViewModel()
        : this([
            CsvGameExportService.CreateRow("Steam/GOG", "The Witcher 3", "Steam:292030; GOG:1207664643"),
            CsvGameExportService.CreateRow("Epic", "Alan Wake", "Epic:ns/cat"),
            CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400")
        ])
    {
    }

    public IReadOnlyList<LibraryFilterOption> Filters { get; }

    public IReadOnlyList<LibrarySortOption> Sorts { get; }

    public bool HasVisibleGames => VisibleGames.Count > 0;

    /// <summary>"Showing 12 of 340 games".</summary>
    public string Summary => VisibleGames.Count == _all.Count
        ? $"{_all.Count} games"
        : $"Showing {VisibleGames.Count} of {_all.Count} games";

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnSelectedFilterChanged(LibraryFilterOption value) => ApplyFilter();

    partial void OnSelectedSortChanged(LibrarySortOption value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();
        var key = SelectedFilter?.Key ?? AllKey;

        var filtered = _all
            .Where(g => key switch
            {
                AllKey => true,
                MultiStoreKey => g.IsMultiStore,
                NeverPlayedKey => g.IsNeverPlayed,
                _ => g.Platforms.Contains(key)
            })
            .Where(g => string.IsNullOrEmpty(query) ||
                        g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        // _all is already name-ordered and OrderBy is stable, so ties stay alphabetical.
        VisibleGames = (SelectedSort?.Sort switch
        {
            LibrarySort.MostPlayed => filtered.OrderByDescending(g => g.PlaytimeMinutes ?? -1),
            LibrarySort.RecentlyPlayed => filtered.OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue),
            _ => filtered
        }).ToList();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasVisibleGames));
    }
}
