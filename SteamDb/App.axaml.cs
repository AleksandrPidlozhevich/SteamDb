using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SteamDb.ViewModels;
using SteamDb.Views;

namespace SteamDb;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Build();
            desktop.MainWindow = new MainWindow
            {
                DataContext = services.GetRequiredService<MainWindowViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}