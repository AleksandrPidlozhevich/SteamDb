using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using SteamDb.ViewModels;

namespace SteamDb.Views;

/// <summary>Viewer of the fetched library (see <see cref="ViewModels.LibraryViewModel"/>).</summary>
public partial class LibraryWindow : Window
{
    public LibraryWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Double-clicking a game opens its store page (not when the double-click hit the Hide button).
    private void OnGameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        if (DataContext is LibraryViewModel library && sender is ListBox { SelectedItem: LibraryGameItem game })
            library.OpenStorePageCommand.Execute(game);
    }
}
