using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using SteamDb.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>
/// <see cref="IDialogService"/> backed by the application's main window. Resolves the window
/// lazily (it may not exist yet when the service is constructed) and no-ops gracefully if it isn't.
/// </summary>
public sealed class WindowDialogService : IDialogService
{
    private static readonly FilePickerFileType CsvFileType =
        new("CSV files") { Patterns = ["*.csv"] };

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public IClipboard? Clipboard => MainWindow?.Clipboard;

    public async Task<IStorageFile?> PickSaveCsvAsync(string suggestedName)
    {
        var window = MainWindow;
        if (window == null) return null;

        return await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            Title = "Save CSV File",
            FileTypeChoices = [CsvFileType]
        });
    }

    public async Task<IStorageFile?> PickOpenCsvAsync(string title)
    {
        var window = MainWindow;
        if (window == null) return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [CsvFileType]
        });

        return files.FirstOrDefault();
    }

    public async Task ShowErrorAsync(string title, Exception exception)
    {
        var window = MainWindow;
        if (window == null) return;

        var errorWindow = new Error(title, BuildErrorMessage(exception));
        await errorWindow.ShowDialog(window);
    }

    public void OpenLink(string url)
    {
        SystemBrowser.Open(url);
    }

    private static string BuildErrorMessage(Exception exception)
    {
        var messages = new List<string>();
        var current = exception;
        while (current != null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.Message);
            current = current.InnerException;
        }

        return messages.Count == 0 ? "Unknown error" : string.Join(Environment.NewLine, messages.Distinct());
    }
}
