using System;
using System.IO;

namespace SteamDb.Services;

/// <summary>
/// Where the app keeps its per-user data (logs, encrypted secrets). This is deliberately not the
/// install folder: Velopack replaces that folder on every update, and inside a macOS .app bundle it
/// may not be writable at all.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// %AppData%\SteamDb on Windows, ~/Library/Application Support/SteamDb on macOS,
    /// ~/.config/SteamDb on Linux.
    /// </summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamDb");

    /// <summary>The folder the app was started from (where pre-Velopack builds kept their data).</summary>
    public static string ExeDirectory => AppContext.BaseDirectory;
}
