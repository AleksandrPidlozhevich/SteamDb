using Avalonia.Markup.Xaml;

namespace Pilewise.Views;

/// <summary>Duplicates dialog (Notion pages / Sheets rows). <c>ShowDialog&lt;bool&gt;</c> returns true only on confirm.</summary>
public partial class DuplicatesWindow : DialogWindow
{
    public DuplicatesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
