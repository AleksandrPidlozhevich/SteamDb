using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SteamDb;

public partial class Error : Window
{
    public string Message { get; private set; } = "";

    public Error()
    {
        InitializeComponent();
        DataContext = this;
    }

    public Error(string title, string message)
    {
        InitializeComponent();
        Title = title;
        Message = message ?? "";
        DataContext = this;
    }

    private void CloseButtonClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
