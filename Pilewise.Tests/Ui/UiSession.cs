using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Pilewise.Tests.Ui;

/// <summary>
/// Runs Avalonia headlessly (real Skia rendering, no screen) on one dedicated UI thread for all UI
/// tests: the app's styles load once, and each test sends its work there. The thread is STA on Windows
/// because the main window hosts a WebView2. Binding errors are collected so a test can fail on them.
/// </summary>
internal static class UiSession
{
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly Lazy<Thread> Thread = new(Start);
    private static readonly BindingErrorSink Errors = new();

    /// <summary>Where rendered windows are saved (next to the test assembly) for a look by eye.</summary>
    public static string SnapshotDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "ui-snapshots");

    /// <summary>Runs <paramref name="work"/> on the UI thread and returns its result.</summary>
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        _ = Thread.Value;
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() =>
        {
            try
            {
                result.SetResult(work());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        return result.Task;
    }

    public static Task RunAsync(Action work) => RunAsync(() =>
    {
        work();
        return true;
    });

    /// <summary>Lets queued UI work (layout, bindings, input) and a few render ticks run.</summary>
    public static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Switches the app theme (call before creating the window).</summary>
    public static void UseTheme(ThemeVariant variant) => Application.Current!.RequestedThemeVariant = variant;

    /// <summary>Shows the window, lays it out and returns it (close it when done).</summary>
    public static T Show<T>(T window) where T : Window
    {
        window.Show();
        Pump();
        return window;
    }

    /// <summary>Renders the window, saves the frame as <c>ui-snapshots/&lt;name&gt;.png</c> and returns
    /// whether a frame was produced.</summary>
    public static bool Snapshot(Window window, string name) =>
        SaveFrame(window, Path.Combine(SnapshotDirectory, name + ".png"));

    /// <summary>Renders the window and saves the frame as a PNG at <paramref name="path"/>.</summary>
    public static bool SaveFrame(Window window, string path)
    {
        Pump();
        var frame = window.CaptureRenderedFrame();
        if (frame == null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame.Save(path, PngBitmapEncoderOptions.Default);
        return true;
    }

    /// <summary>Binding errors logged since the last <see cref="ClearBindingErrors"/>.</summary>
    public static IReadOnlyList<string> BindingErrors => Errors.Messages;

    public static void ClearBindingErrors() => Errors.Clear();

    private static Thread Start()
    {
        var ready = new ManualResetEventSlim();
        Exception? setupError = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .WithInterFont()
                    .SetupWithoutStarting();
                Logger.Sink = Errors;
            }
            catch (Exception ex)
            {
                setupError = ex;
            }

            ready.Set();
            foreach (var work in Work.GetConsumingEnumerable()) work();
        })
        {
            IsBackground = true,
            Name = "Avalonia UI tests"
        };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (setupError != null) throw new InvalidOperationException("Avalonia headless setup failed.", setupError);
        return thread;
    }

    // Collects warnings and errors from the binding system (a property that doesn't exist, a wrong type…).
    private sealed class BindingErrorSink : ILogSink
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToList();

        public void Clear() => _messages.Clear();

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            _messages.Enqueue($"{source?.GetType().Name}: {messageTemplate}");

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues) =>
            _messages.Enqueue($"{source?.GetType().Name}: {messageTemplate} [{string.Join(", ", propertyValues)}]");
    }
}
