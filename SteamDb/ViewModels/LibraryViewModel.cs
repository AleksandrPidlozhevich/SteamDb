using CommunityToolkit.Mvvm.ComponentModel;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamDb.ViewModels;

/// <summary>One game in the library viewer: the merged row flattened for display.</summary>
public sealed record LibraryGameItem(string Name, IReadOnlyList<string> Platforms, string Id)
{
    public static LibraryGameItem From(CsvGameExportRow row)
    {
        var platforms = PlatformCatalog.All.Where(d => d.Has(row)).Select(d => d.Label).ToList();
        if (row.IsGamePass) platforms.Add(Services.Platforms.GamePass);
        return new LibraryGameItem(row.Name, platforms, row.IdText);
    }

    /// <summary>Owned on two or more stores (Game Pass is a tag, not a purchase).</summary>
    public bool IsMultiStore => Platforms.Count(p => p != Services.Platforms.GamePass) > 1;
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

    private readonly IReadOnlyList<LibraryGameItem> _all;

    [ObservableProperty] private string? searchText;

    [ObservableProperty] private LibraryFilterOption selectedFilter;

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

        Filters = options;
        selectedFilter = options[0];
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

    public bool HasVisibleGames => VisibleGames.Count > 0;

    /// <summary>"Showing 12 of 340 games".</summary>
    public string Summary => VisibleGames.Count == _all.Count
        ? $"{_all.Count} games"
        : $"Showing {VisibleGames.Count} of {_all.Count} games";

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnSelectedFilterChanged(LibraryFilterOption value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();
        var key = SelectedFilter?.Key ?? AllKey;

        VisibleGames = _all
            .Where(g => key switch
            {
                AllKey => true,
                MultiStoreKey => g.IsMultiStore,
                _ => g.Platforms.Contains(key)
            })
            .Where(g => string.IsNullOrEmpty(query) ||
                        g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasVisibleGames));
    }
}
