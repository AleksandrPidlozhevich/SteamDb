using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SteamDb.Views;

/// <summary>
/// A main-window action card: the content (buttons, "Last sync" lines…) in a framed box whose left
/// border is broken by <see cref="Icon"/>. The template lives in <c>Styles/Common.axaml</c>.
/// </summary>
public class ActionCard : ContentControl
{
    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<ActionCard, IImage?>(nameof(Icon));

    public IImage? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
