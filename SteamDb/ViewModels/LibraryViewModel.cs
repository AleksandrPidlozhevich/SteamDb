using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    DateTimeOffset? LastPlayed,
    bool IsHidden = false,
    CsvGameExportRow? Row = null)
{
    public static LibraryGameItem From(CsvGameExportRow row, bool hidden = false)
    {
        var platforms = PlatformCatalog.All.Where(d => d.Has(row)).Select(d => d.Label).ToList();
        if (row.IsGamePass) platforms.Add(Services.Platforms.GamePass);
        return new LibraryGameItem(row.Name, platforms, row.IdText, row.PlaytimeMinutes, row.LastPlayed, hidden, row);
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

    public string HideLabel => IsHidden ? "Unhide" : "Hide";

    public string HideTip => IsHidden
        ? "Include this game in exports again"
        : "Leave this game out of every export (demos, test servers, tools…)";
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
/// with per-platform counts. Nothing here writes to an export target; the only change it makes is
/// hiding / unhiding games (<see cref="IgnoredGames"/>), which the next exports honour.
/// </summary>
public sealed partial class LibraryViewModel : ViewModelBase
{
    public const string AllKey = "*";
    public const string MultiStoreKey = "multi";
    public const string NeverPlayedKey = "never";
    public const string HiddenKey = "hidden";

    private readonly List<LibraryGameItem> _all;
    private readonly IgnoredGames? _ignored;

    [ObservableProperty] private string? searchText;

    [ObservableProperty] private LibraryFilterOption selectedFilter;

    [ObservableProperty] private LibrarySortOption selectedSort;

    [ObservableProperty] private IReadOnlyList<LibraryGameItem> visibleGames = [];

    [ObservableProperty] private IReadOnlyList<LibraryFilterOption> filters = [];

    /// <param name="rows">The games that will be exported.</param>
    /// <param name="hidden">Fetched games the user hid (shown under the "Hidden" filter).</param>
    /// <param name="ignored">Where Hide / Unhide is saved; null makes the viewer read-only.</param>
    public LibraryViewModel(
        IEnumerable<CsvGameExportRow> rows, IEnumerable<CsvGameExportRow>? hidden = null, IgnoredGames? ignored = null)
    {
        _ignored = ignored;
        _all = rows.Where(r => r.HasContent).Select(r => LibraryGameItem.From(r))
            .Concat((hidden ?? []).Where(r => r.HasContent).Select(r => LibraryGameItem.From(r, true)))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Sorts =
        [
            new LibrarySortOption("Sort: name", LibrarySort.Name),
            new LibrarySortOption("Sort: most played", LibrarySort.MostPlayed),
            new LibrarySortOption("Sort: recently played", LibrarySort.RecentlyPlayed)
        ];
        selectedSort = Sorts[0];
        filters = BuildFilters();
        selectedFilter = filters[0];
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

    public IReadOnlyList<LibrarySortOption> Sorts { get; }

    public bool HasVisibleGames => VisibleGames.Count > 0;

    /// <summary>Hide / Unhide buttons are shown only when the choice can be saved.</summary>
    public bool CanHide => _ignored != null;

    private int ExportedCount => _all.Count(g => !g.IsHidden);

    /// <summary>"Showing 12 of 340 games" (hidden games aren't counted).</summary>
    public string Summary
    {
        get
        {
            var hidden = _all.Count - ExportedCount;
            var suffix = hidden > 0 ? $" · {hidden} hidden" : "";
            return VisibleGames.Count == ExportedCount && SelectedFilter?.Key != HiddenKey
                ? $"{ExportedCount} games{suffix}"
                : $"Showing {VisibleGames.Count} of {ExportedCount} games{suffix}";
        }
    }

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnSelectedFilterChanged(LibraryFilterOption value) => ApplyFilter();

    partial void OnSelectedSortChanged(LibrarySortOption value) => ApplyFilter();

    /// <summary>Hides a game from every export, or brings a hidden one back.</summary>
    [RelayCommand]
    private void ToggleHidden(LibraryGameItem? item)
    {
        if (item?.Row == null || _ignored == null) return;

        if (item.IsHidden) _ignored.Unignore(item.Row);
        else _ignored.Ignore(item.Row);

        var index = _all.IndexOf(item);
        if (index >= 0) _all[index] = item with { IsHidden = !item.IsHidden };

        // Rebuild the counts but stay on the same filter.
        var key = SelectedFilter?.Key ?? AllKey;
        Filters = BuildFilters();
        SelectedFilter = Filters.FirstOrDefault(f => f.Key == key) ?? Filters[0];
        ApplyFilter();
    }

    private List<LibraryFilterOption> BuildFilters()
    {
        var exported = _all.Where(g => !g.IsHidden).ToList();
        var options = new List<LibraryFilterOption> { new("All platforms", AllKey, exported.Count) };
        foreach (var label in PlatformCatalog.All.Select(d => d.Label).Append(Services.Platforms.GamePass))
        {
            var count = exported.Count(g => g.Platforms.Contains(label));
            if (count > 0) options.Add(new LibraryFilterOption(label, label, count));
        }

        var multi = exported.Count(g => g.IsMultiStore);
        if (multi > 0) options.Add(new LibraryFilterOption("Owned on 2+ stores", MultiStoreKey, multi));

        var never = exported.Count(g => g.IsNeverPlayed);
        if (never > 0) options.Add(new LibraryFilterOption("Never played", NeverPlayedKey, never));

        var hidden = _all.Count - exported.Count;
        if (hidden > 0) options.Add(new LibraryFilterOption("Hidden", HiddenKey, hidden));

        return options;
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();
        var key = SelectedFilter?.Key ?? AllKey;

        var filtered = _all
            .Where(g => key == HiddenKey ? g.IsHidden : !g.IsHidden)
            .Where(g => key switch
            {
                AllKey or HiddenKey => true,
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
