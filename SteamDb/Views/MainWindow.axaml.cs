using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using SteamDb.Models;
using SteamDb.Services;
using SteamDb.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SteamDb.Views;

public partial class MainWindow : Window
{
    private readonly IStoreClientFactory? _clients;
    private readonly ILogService? _log;
    private readonly ISecretStore? _secrets;

    // Parameterless ctor for the XAML loader / previewer (no pre-warm without DI dependencies).
    public MainWindow()
    {
        InitializeComponent();

        // Drop a game list / settings file anywhere on the window.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public MainWindow(IStoreClientFactory clients, ILogService log, ISecretStore secrets) : this()
    {
        _clients = clients;
        _log = log;
        _secrets = secrets;
        RestorePlacement();
        _ = PrewarmGogLoginAsync();
    }

    // Reopens where it was closed (size, position, maximized), unless that spot is off every screen
    // now (e.g. a monitor was unplugged): then the remembered size is kept and the window is centred.
    private void RestorePlacement()
    {
        try
        {
            var saved = WindowPlacement.Parse(_secrets!.Load(WindowPlacement.SecretKey));
            if (saved == null) return;

            var screens = Screens.All
                .Select(s => new ScreenArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))
                .ToList();
            var scaling = Screens.Primary?.Scaling ?? 1;
            var (width, height, keepPosition) = saved.Fit(screens, scaling, MinWidth, MinHeight);

            Width = width;
            Height = height;
            if (keepPosition)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(saved.X, saved.Y);
            }

            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            _log?.WriteWarning($"Couldn't restore the window placement: {ex.Message}");
        }
    }

    /// <summary>Remembers the size / position / maximized state (called when the window closes or hides).</summary>
    public void SavePlacement()
    {
        if (_secrets == null || !IsVisible) return;
        try
        {
            var maximized = WindowState == WindowState.Maximized;
            // A maximized window's bounds are the screen's; keep the last normal size instead.
            var previous = WindowPlacement.Parse(_secrets.Load(WindowPlacement.SecretKey));
            var placement = maximized && previous != null
                ? previous with { Maximized = true }
                : new WindowPlacement(Position.X, Position.Y, Width, Height, maximized);
            if (WindowState == WindowState.Minimized) return;
            _secrets.Save(WindowPlacement.SecretKey, placement.Serialize());
        }
        catch (Exception ex)
        {
            _log?.WriteWarning($"Couldn't save the window placement: {ex.Message}");
        }
    }

    // Flips between light and dark. Starting from "Default" (system), ActualThemeVariant tells us
    // what's currently shown, so the first click switches to the opposite explicit variant.
    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant =
            app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var acceptsFile = e.DataTransfer.Contains(DataFormat.File) &&
                          DataContext is MainWindowViewModel { Progress.IsBusy: false };
        e.DragEffects = acceptsFile ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = acceptsFile;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (DataContext is not MainWindowViewModel viewModel) return;

        // One file at a time: the first file of the drop (folders are ignored).
        var file = Array.Find(e.DataTransfer.TryGetFiles() ?? [], item => item is IStorageFile) as IStorageFile;
        if (file == null) return;

        try
        {
            await viewModel.OpenDroppedFileAsync(file);
        }
        catch (Exception ex)
        {
            // async void: never let an exception escape to the dispatcher.
            _log?.WriteException(ex, "Error handling a dropped file");
        }
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
