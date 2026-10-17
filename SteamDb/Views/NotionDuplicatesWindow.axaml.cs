using Avalonia.Markup.Xaml;

namespace SteamDb.Views;

/// <summary>Duplicate-pages dialog. <c>ShowDialog&lt;bool&gt;</c> returns true only on confirm.</summary>
public partial class NotionDuplicatesWindow : DialogWindow
{
    public NotionDuplicatesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
