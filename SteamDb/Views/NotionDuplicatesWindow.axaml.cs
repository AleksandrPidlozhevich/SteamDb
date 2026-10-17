using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SteamDb.Views;

/// <summary>Duplicate-pages dialog. <c>ShowDialog&lt;bool&gt;</c> returns true only on confirm.</summary>
public partial class NotionDuplicatesWindow : Window
{
    public NotionDuplicatesWindow()
    {
        InitializeComponent();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
