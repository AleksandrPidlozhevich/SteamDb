using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SteamDb.Views;

/// <summary>Notion dry-run dialog. <c>ShowDialog&lt;bool&gt;</c> returns true only on confirm.</summary>
public partial class NotionExportPreviewWindow : Window
{
    public NotionExportPreviewWindow()
    {
        InitializeComponent();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
