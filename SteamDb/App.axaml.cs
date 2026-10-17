using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SteamDb.Services;
using SteamDb.ViewModels;
using SteamDb.Views;
using System;
using System.Linq;

namespace SteamDb;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _quitting;

    /// <summary>Set by <c>Program.Main</c>: another start asks this one to show its window.</summary>
    public static SingleInstance? Instance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var services = AppServices.Build();
            var window = services.GetRequiredService<MainWindow>();
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            window.DataContext = viewModel;
            _window = window;

            // The app lives until "Quit" (tray) or a real close: closing to the tray only hides the window.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closing += (_, e) =>
            {
                window.SavePlacement();
                viewModel.SaveOnExit();

                // Only a user close goes to the tray; logoff / shutdown / Quit really close.
                if (!_quitting && viewModel.KeepsRunningOnClose &&
                    e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };
            window.Closed += (_, _) => desktop.Shutdown();

            viewModel.IsTrayAvailable = TraySupport.IsAvailable();
            CreateTrayIcon(viewModel);
            viewModel.ShowWindowRequested += ShowMainWindow;
            if (Instance != null) Instance.ShowRequested += () => Dispatcher.UIThread.Post(ShowMainWindow);

            // Started at login (--minimized): stay in the tray. Assigning MainWindow after start-up
            // keeps the lifetime from showing it, while dialogs can still find it. Without a tray
            // the window opens normally, or there'd be no way to reach it.
            if (viewModel.IsTrayAvailable && desktop.Args?.Contains(AutoStart.MinimizedArgument) == true)
                Dispatcher.UIThread.Post(() => desktop.MainWindow = window);
            else
                desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Tray icon on every platform (Linux needs a desktop with StatusNotifier / AppIndicator support):
    // click to open; menu Open / Sync now / Quit. The tooltip shows the last result.
    private void CreateTrayIcon(MainWindowViewModel viewModel)
    {
        var open = new NativeMenuItem("Open SteamDb");
        open.Click += (_, _) => ShowMainWindow();
        var syncNow = new NativeMenuItem("Sync now");
        syncNow.Click += async (_, _) => await viewModel.SyncNowAsync();
        var history = new NativeMenuItem("Sync history…");
        history.Click += (_, _) => viewModel.ShowSyncHistory();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SteamDb/Assets/gamedate.ico"))),
            ToolTipText = "SteamDb",
            Menu = new NativeMenu { Items = { open, syncNow, history, new NativeMenuItemSeparator(), quit } }
        };
        _tray.Clicked += (_, _) => ShowMainWindow();

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.ResultMessage) && _tray != null)
                _tray.ToolTipText = viewModel.ResultMessage is { } message
                    ? Truncate($"SteamDb — {message}", 120)
                    : "SteamDb";
        };

        TrayIcon.SetIcons(this, [_tray]);
    }

    private void ShowMainWindow()
    {
        if (_window == null || _desktop == null) return;
        _desktop.MainWindow ??= _window;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _quitting = true;
        if (_window is { IsVisible: true }) _window.Close();
        else
        {
            if (_window?.DataContext is MainWindowViewModel viewModel) viewModel.SaveOnExit();
            _desktop?.Shutdown();
        }
    }

    // Some platforms cap tray tooltips (Windows: 127 chars).
    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
