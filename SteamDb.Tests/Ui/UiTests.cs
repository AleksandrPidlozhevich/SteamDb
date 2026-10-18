using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SteamDb.Models;
using SteamDb.Services;
using SteamDb.ViewModels;
using SteamDb.Views;
using Xunit;

namespace SteamDb.Tests.Ui;

/// <summary>
/// Renders every window headlessly in both themes (snapshots in <c>ui-snapshots/</c>) and checks layout
/// and keyboard behaviour that unit tests of the view models can't see.
/// </summary>
public class UiTests
{
    public static TheoryData<string> Windows => new()
    {
        "library", "history", "notion-preview", "duplicates-notion", "duplicates-sheets", "error",
        "main-sources", "main-export", "main-automation", "main-settings", "main-collapsed"
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public async Task Window_RendersInBothThemes_WithoutBindingErrors(string name)
    {
        var errors = await UiSession.RunAsync(() =>
        {
            var found = new List<string>();
            foreach (var (variant, suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                UiSession.UseTheme(variant);
                UiSession.ClearBindingErrors();
                var window = UiSession.Show(Create(name));
                Assert.True(UiSession.Snapshot(window, $"{name}-{suffix}"), $"{name} produced no frame");
                found.AddRange(UiSession.BindingErrors.Select(e => $"{name}-{suffix}: {e}"));
                window.Close();
            }

            return found;
        });

        // Full messages (Assert.Empty would cut them off).
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public Task Library_ColumnHeaders_LineUpWithTheRows() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("library"));
        try
        {
            foreach (var (header, column) in new[] { ("PLATFORM", 1), ("PLAYTIME", 2), ("LAST PLAYED", 3), ("ID", 4) })
            {
                var headerBlock = Descendants<TextBlock>(window).Single(t => t.Text == header);
                var firstRow = Descendants<ListBoxItem>(window).First();
                var cell = Descendants<Grid>(firstRow).First().Children.Single(c => Grid.GetColumn(c) == column);

                Assert.InRange(X(cell, window) - X(headerBlock, window), -2, 2);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Export_NotionFields_SitOnOneRow() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("main-export"));
        try
        {
            var token = Descendants<TextBox>(window).Single(t => t.PlaceholderText == "Notion integration token");
            var database = Descendants<TextBox>(window).Single(t => t.PlaceholderText == "Database ID");

            Assert.Equal(Y(token, window), Y(database, window), 1);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Shortcuts_CommandNumber_SwitchesPage() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("main-sources"));
        try
        {
            var viewModel = (MainWindowViewModel)window.DataContext!;

            window.KeyPress(Key.D3, CommandModifier, PhysicalKey.Digit3, "3");
            UiSession.Pump();
            Assert.Equal((int)MainTab.Automation, viewModel.SelectedTab);

            window.KeyPress(Key.D1, CommandModifier, PhysicalKey.Digit1, "1");
            UiSession.Pump();
            Assert.Equal((int)MainTab.Sources, viewModel.SelectedTab);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Library_Escape_ClearsTheSearch_ThenCloses() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("library"));
        var library = (LibraryViewModel)window.DataContext!;
        library.SearchText = "hades";
        UiSession.Pump();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        UiSession.Pump();
        Assert.True(string.IsNullOrEmpty(library.SearchText));
        Assert.True(window.IsVisible);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        UiSession.Pump();
        Assert.False(window.IsVisible);
    });

    [Fact]
    public Task Dialog_Escape_ClosesWithFalse_AndConfirmClosesWithTrue() => UiSession.RunAsync(() =>
    {
        var owner = UiSession.Show(new Window());
        try
        {
            var escaped = new DuplicatesWindow { DataContext = Duplicates("duplicates-notion") };
            var escapedResult = escaped.ShowDialog<bool>(owner);
            UiSession.Pump();
            escaped.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            UiSession.Pump();
            Assert.True(escapedResult.IsCompleted);
            Assert.False(escapedResult.Result);

            var confirmed = new DuplicatesWindow { DataContext = Duplicates("duplicates-notion") };
            var confirmedResult = confirmed.ShowDialog<bool>(owner);
            UiSession.Pump();
            Descendants<Button>(confirmed).Single(b => b.Classes.Contains(DialogWindow.ConfirmClass))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiSession.Pump();
            Assert.True(confirmedResult.IsCompleted);
            Assert.True(confirmedResult.Result);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task Sidebar_Collapsed_ShowsIconsOnly() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("main-collapsed"));
        try
        {
            var navTexts = Descendants<TextBlock>(window)
                .Where(t => t.Text is "Sources" or "Export" or "Automation" or "Settings" &&
                            t.FindAncestorOfType<ListBoxItem>() != null)
                .ToList();

            Assert.Equal(4, navTexts.Count);
            Assert.All(navTexts, t => Assert.False(t.IsVisible));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// Regenerates the README's main-window screenshots (made-up data, no real keys or ids) when
    /// <c>STEAMDB_README_SCREENSHOTS</c> names the output folder, e.g. <c>docs/images</c>; otherwise it
    /// does nothing.
    /// </summary>
    [Fact]
    public Task ReadmeScreenshots_WhenAsked()
    {
        var folder = Environment.GetEnvironmentVariable("STEAMDB_README_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return Task.CompletedTask;

        return UiSession.RunAsync(() =>
        {
            foreach (var (variant, file) in new[] { (ThemeVariant.Light, "main-window.png"), (ThemeVariant.Dark, "main-window-dark.png") })
            {
                UiSession.UseTheme(variant);
                var viewModel = new MainWindowViewModel
                {
                    SelectedTab = (int)MainTab.Export,
                    SteamApiKey = "0123456789ABCDEF0123456789ABCDEF",
                    SteamId = "76561198000000000",
                    NotionToken = "ntn_example_integration_token",
                    DbId = "0123456789abcdef0123456789abcdef",
                    GoogleSheetsTableName = "My game backlog",
                    CsvLastSync = "Last sync: today 14:20",
                    NotionLastSync = "Last sync: today 14:20",
                    NotionProgress = "Completed 137 of 386 (35%)"
                };
                var window = UiSession.Show(new MainWindow { DataContext = viewModel, Width = 900, Height = 640 });
                Assert.True(UiSession.SaveFrame(window, System.IO.Path.Combine(folder, file)));
                window.Close();
            }
        });
    }

    // ---- windows with sample data ---------------------------------------------------------------

    private static Window Create(string name) => name switch
    {
        "library" => new LibraryWindow { DataContext = Library() },
        "history" => new SyncLogWindow { DataContext = new SyncLogViewModel() },
        "notion-preview" => new NotionExportPreviewWindow { DataContext = new NotionExportPreviewViewModel() },
        "duplicates-notion" or "duplicates-sheets" => new DuplicatesWindow { DataContext = Duplicates(name) },
        "error" => new ErrorWindow("Export error in Notion",
            "Could not find database with ID: 1234abcd.\nMake sure the database is shared with your integration."),
        "main-sources" => MainWindowOn(MainTab.Sources),
        "main-export" => MainWindowOn(MainTab.Export),
        "main-automation" => MainWindowOn(MainTab.Automation),
        "main-settings" => MainWindowOn(MainTab.Settings),
        "main-collapsed" => MainWindowOn(MainTab.Export, collapsed: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    private static MainWindow MainWindowOn(MainTab tab, bool collapsed = false)
    {
        var viewModel = new MainWindowViewModel { SelectedTab = (int)tab, IsSidebarCollapsed = collapsed };
        return new MainWindow { DataContext = viewModel, Width = 900, Height = 640 };
    }

    private static LibraryViewModel Library()
    {
        var rows = new[]
        {
            Row("Steam/GOG", "The Witcher 3: Wild Hunt", "Steam:292030; GOG:1207664643", 6200),
            Row("Steam", "Portal 2", "Steam:620", 750),
            Row("Steam", "Hades", "Steam:1145360", 0),
            Row("Epic", "Alan Wake 2", "Epic:ns/cat"),
            Row("GOG", "Disco Elysium", "GOG:1771589310"),
            Row("Xbox/Game Pass", "Starfield", "Xbox:1234", 300)
        };
        var backlog = new BacklogSnapshot("Notion", DateTimeOffset.Now, 1, 6, ["Steam:620"]);
        return new LibraryViewModel(rows, [Row("Steam", "Test Server", "Steam:1")], new IgnoredGames(), backlog,
            new LibraryActions(_ => { }, _ => Task.CompletedTask, _ => Task.FromResult<BacklogSnapshot?>(backlog), "Notion"),
            addedOn: r => r.Name == "Hades" ? DateOnly.FromDateTime(DateTime.Today) : null);
    }

    private static DuplicatesViewModel Duplicates(string name)
    {
        if (name == "duplicates-sheets")
        {
            var rows = new[]
            {
                new SheetRow(1, Row("Steam", "Hades", "Steam:1145360"), "Steam", "Steam:1145360", false),
                new SheetRow(7, Row("Epic", "Hades", "Epic:ns/cat"), "Epic", "Epic:ns/cat", true)
            };
            return DuplicatesViewModel.ForSheets(SheetDuplicateFinder.Find(rows));
        }

        var pages = new[]
        {
            Page("old", "Steam", "Witcher 3", "Steam:292030", -30),
            Page("new", "GOG", "Witcher 3", "GOG:1207664643", -1)
        };
        return DuplicatesViewModel.ForNotion(NotionDuplicateFinder.Find(pages));
    }

    private static NotionGameRow Page(string id, string platform, string name, string gameId, int days) =>
        new(Row(platform, name, gameId), id, new HashSet<string>([platform]), gameId,
            CreatedTime: DateTimeOffset.Now.AddDays(days));

    private static CsvGameExportRow Row(string platform, string name, string id, int? minutes = null)
    {
        var row = CsvGameExportService.CreateRow(platform, name, id);
        row.PlaytimeMinutes = minutes;
        return row;
    }

    // ---- helpers --------------------------------------------------------------------------------

    // The headless platform's command key (Ctrl; tests run on Windows / Linux CI).
    private static RawInputModifiers CommandModifier =>
        OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static IEnumerable<T> Descendants<T>(Visual root) => root.GetVisualDescendants().OfType<T>();

    private static double X(Visual visual, Visual relativeTo) => visual.TranslatePoint(default, relativeTo)!.Value.X;

    private static double Y(Visual visual, Visual relativeTo) => visual.TranslatePoint(default, relativeTo)!.Value.Y;
}
