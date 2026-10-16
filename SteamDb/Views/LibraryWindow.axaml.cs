using Avalonia.Controls;

namespace SteamDb.Views;

/// <summary>Read-only viewer of the fetched library (see <see cref="ViewModels.LibraryViewModel"/>).</summary>
public partial class LibraryWindow : Window
{
    public LibraryWindow()
    {
        InitializeComponent();
    }
}
