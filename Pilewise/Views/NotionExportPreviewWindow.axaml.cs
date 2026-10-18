using Avalonia.Markup.Xaml;

namespace Pilewise.Views;

/// <summary>Notion dry-run dialog. <c>ShowDialog&lt;bool&gt;</c> returns true only on confirm.</summary>
public partial class NotionExportPreviewWindow : DialogWindow
{
    public NotionExportPreviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
