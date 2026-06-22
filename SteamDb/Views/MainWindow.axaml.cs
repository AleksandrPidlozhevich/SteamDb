using Avalonia.Controls;
using Avalonia.Threading;
using SteamDb.Models;
using SteamDb.Services;
using System;
using System.Threading.Tasks;

namespace SteamDb.Views;

public partial class MainWindow : Window
{
    private readonly IStoreClientFactory? _clients;
    private readonly ILogService? _log;

    // Parameterless ctor for the XAML loader / previewer (no pre-warm without DI dependencies).
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(IStoreClientFactory clients, ILogService log) : this()
    {
        _clients = clients;
        _log = log;
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
                PrewarmWebView.Source = _clients!.CreateGog().LoginRequestUri);
        }
        catch (Exception ex)
        {
            _log!.WriteWarning($"GOG login pre-warm failed: {ex.Message}");
        }
    }
}
