using System;
using System.Diagnostics;

namespace SteamDb.Services;

/// <summary>Opens a URL in the user's default browser, cross-platform.</summary>
public static class SystemBrowser
{
    public static void Open(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", url);
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
    }
}