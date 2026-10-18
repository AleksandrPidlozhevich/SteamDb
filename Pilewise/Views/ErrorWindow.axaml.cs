using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Pilewise.Views;

/// <summary>Modal error dialog: the message (inner exceptions included) with Copy and OK.</summary>
public partial class ErrorWindow : DialogWindow
{
    /// <summary>For the XAML loader / previewer.</summary>
    public ErrorWindow()
        : this("Error", "")
    {
    }

    public ErrorWindow(string title, string message)
    {
        Message = message;
        DataContext = this;
        AvaloniaXamlLoader.Load(this);
        Title = title; // after Load, which sets the XAML default
    }

    public string Message { get; }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(Message);
    }
}
