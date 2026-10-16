using Avalonia;
using System;
using Velopack;

namespace SteamDb;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack's install/update/uninstall hooks (which exit right away)
        // and finishes applying a downloaded update. A no-op when not installed via Velopack.
        VelopackApp.Build().Run();

        // --disable-gpu-compositing: fixes a black-screen issue in this windowing setup.
        // --disable-logging: silences harmless Chromium teardown noise on the console
        //   (e.g. "Failed to unregister class Chrome_WidgetWin_0").
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu-compositing --disable-logging");

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}