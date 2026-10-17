using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SteamDb.Views;

/// <summary>Duplicate-pages dialog. <c>ShowDialog&lt;bool&gt;</c> returns true only on confirm.</summary>
public partial class NotionDuplicatesWindow : Window
{
    public NotionDuplicatesWindow()
    {
        // Loaded directly rather than through the generated InitializeComponent: the window has no
        // named controls, and this way it doesn't depend on the IDE having run the source generator.
        AvaloniaXamlLoader.Load(this);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
