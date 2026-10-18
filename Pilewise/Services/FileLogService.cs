using System;
using System.Collections.Generic;
using System.IO;

namespace SteamDb.Services;

/// <summary>
/// <see cref="ILogService"/> that appends to a daily log file in a <c>DbSteam_Log</c> folder in the
/// per-user data folder (<see cref="AppPaths"/>). Writes a start banner on construction; thread-safe via a single lock.
/// </summary>
public sealed class FileLogService : ILogService
{
    private const string LogFolderName = "DbSteam_Log";
    private const string ShortDateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Folder holding the daily log files (opened by the "Logs" link).</summary>
    public static string LogDirectory => Path.Combine(AppPaths.DataDirectory, LogFolderName);

    private readonly object _lock = new();
    private readonly string _logFilePath;

    public FileLogService()
    {
        var logDirectory = LogDirectory;
        if (!Directory.Exists(logDirectory))
            Directory.CreateDirectory(logDirectory);

        var dateStr = DateTime.Now.ToString(ShortDateFormat);
        _logFilePath = Path.Combine(logDirectory, $"{dateStr}_DbSteam.log");

        var startLines = new List<string>
        {
            "====================================================================================",
            $"SteamDb_start_{DateTime.Now.ToString(TimeFormat)}",
            "===================================================================================="
        };
        File.AppendAllLines(_logFilePath, startLines);
    }

    public void WriteLog(string message, LogLevel level = LogLevel.Info)
    {
        var timestamp = DateTime.Now.ToString(TimeFormat);
        var logEntry = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
        }
    }

    public void WriteInfo(string message) => WriteLog(message);

    public void WriteWarning(string message) => WriteLog(message, LogLevel.Warning);

    public void WriteError(string message) => WriteLog(message, LogLevel.Error);

    public void WriteException(Exception ex, string additionalMessage = "")
    {
        var message = string.IsNullOrEmpty(additionalMessage)
            ? $"Exception: {ex.Message}\nStackTrace: {ex.StackTrace}"
            : $"{additionalMessage}\nException: {ex.Message}\nStackTrace: {ex.StackTrace}";

        WriteLog(message, LogLevel.Error);
    }
}
