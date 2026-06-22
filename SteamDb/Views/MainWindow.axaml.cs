using Avalonia.Controls;
using Avalonia.Threading;
using SteamDb.Models;
using SteamDb.Services;
using System;
using System.Threading.Tasks;

namespace SteamDb.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        _ = PrewarmGogLoginAsync();
    }

    // A few seconds after startup, quietly load the GOG login page into the hidden 1x1 pre-warm
    // WebView. It shares the WebView2 profile/cache with the real login window, so Connect GOG then
    // opens from cache. Invisible to the user; failures are non-fatal.
    private async Task PrewarmGogLoginAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(() =>
                PrewarmWebView.Source = new GogApiClient().LoginRequestUri);
        }
        catch (Exception ex)
        {
            LogService.WriteWarning($"GOG login pre-warm failed: {ex.Message}");
        }
    }
}
