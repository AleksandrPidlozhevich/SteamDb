using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using System;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>
/// Window-level UI affordances the view model needs — file pickers, the error dialog, link
/// opening and clipboard access — kept behind an interface so the view model stays free of
/// Avalonia window plumbing (and testable).
/// </summary>
public interface IDialogService
{
    Task<IStorageFile?> PickSaveCsvAsync(string suggestedName);

    Task<IStorageFile?> PickOpenCsvAsync(string title);

    Task ShowErrorAsync(string title, Exception exception);

    void OpenLink(string url);

    IClipboard? Clipboard { get; }
}
