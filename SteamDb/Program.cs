using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using SteamDb.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading;
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

        // `SteamDb --sync notion …`: sync without opening the window (Task Scheduler, scripts).
        if (SyncCommandLine.IsHeadless(args))
        {
            Environment.ExitCode = RunHeadless(args);
            return;
        }

        // --disable-gpu-compositing: fixes a black-screen issue in this windowing setup.
        // --disable-logging: silences harmless Chromium teardown noise on the console
        //   (e.g. "Failed to unregister class Chrome_WidgetWin_0").
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu-compositing --disable-logging");

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static int RunHeadless(string[] args)
    {
        // A WinExe has no console of its own; borrow the one it was started from (if any) so the
        // output shows up there. Must happen before anything touches Console.
        if (OperatingSystem.IsWindows()) AttachConsole(AttachParentProcess);

        var command = SyncCommandLine.Parse(args);
        if (command.Options == null)
        {
            Console.WriteLine(command.Message);
            return command.ExitCode;
        }

        using var services = AppServices.Build();
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        var sync = new HeadlessSync(
            services.GetRequiredService<ISecretStore>(),
            services.GetRequiredService<IGameLibraryService>(),
            services.GetRequiredService<NotionGameExporter>(),
            services.GetRequiredService<GoogleSheetsGameExporter>(),
            services.GetRequiredService<ILogService>(),
            Console.Out,
            services.GetRequiredService<StoreSelection>());
        return sync.RunAsync(command.Options, cancel.Token).GetAwaiter().GetResult();
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}