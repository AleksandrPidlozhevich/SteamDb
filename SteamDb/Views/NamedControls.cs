using Avalonia.Controls;
using System;

namespace SteamDb.Views;

/// <summary>
/// Looks up an <c>x:Name</c>d control after <c>AvaloniaXamlLoader.Load(this)</c> (the project doesn't
/// use Avalonia's generated fields; see SteamDb.csproj). Fails loudly when the name is missing.
/// </summary>
internal static class NamedControls
{
    public static T Named<T>(this Control view, string name) where T : Control =>
        view.FindControl<T>(name) ??
        throw new InvalidOperationException($"{view.GetType().Name}: no {typeof(T).Name} named \"{name}\" in its XAML.");
}
