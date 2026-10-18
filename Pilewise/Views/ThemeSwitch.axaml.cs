using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SteamDb.Views;

/// <summary>The System / Light / Dark switch (sidebar and Settings page).</summary>
public partial class ThemeSwitch : UserControl
{
    public ThemeSwitch()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
