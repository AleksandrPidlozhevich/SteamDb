using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SteamDb.Services;

public static class LogService
{
    private static readonly string ExeDirectory =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";

    private static readonly string LogFolderName = "DbSteam_Log";
    private static readonly string ShortDateFormat = "yyyy-MM-dd";
    private static readonly string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly object LockObject = new();

    public static bool Initialized { get; private set; }
    public static string LogDirectory { get; private set; } = "";
    public static string LogFilePath { get; private set; } = "";

    public static void Initialize(string className)
    {
        lock (LockObject)
        {
            if (Initialized) return;


            LogDirectory = Path.Combine(ExeDirectory, LogFolderName);

            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);

            var dateStr = DateTime.Now.ToString(ShortDateFormat);
            LogFilePath = Path.Combine(LogDirectory, $"{dateStr}_DbSteam.log");

            var startLines = new List<string>
            {
                "====================================================================================",
                $"{className}_start_{DateTime.Now.ToString(TimeFormat)}",
                "===================================================================================="
            };

            File.AppendAllLines(LogFilePath, startLines);
            Initialized = true;
        }
    }

    public static void WriteLog(string message, LogLevel level = LogLevel.Info)
    {
        lock (LockObject)
        {
            if (string.IsNullOrWhiteSpace(LogFilePath))
                throw new InvalidOperationException(
                    "LogService is not initialized. Call LogService.Initialize() first.");

            var timestamp = DateTime.Now.ToString(TimeFormat);
            var logEntry = $"[{timestamp}] [{level}] {message}";

            File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
        }
    }


    public static void WriteError(string message)
    {
        WriteLog(message, LogLevel.Error);
    }

    public static void WriteWarning(string message)
    {
        WriteLog(message, LogLevel.Warning);
    }

    public static void WriteInfo(string message)
    {
        WriteLog(message);
    }

    public static void WriteDebug(string message)
    {
        WriteLog(message, LogLevel.Debug);
    }

    public static void WriteException(Exception ex, string additionalMessage = "")
    {
        var message = string.IsNullOrEmpty(additionalMessage)
            ? $"Exception: {ex.Message}\nStackTrace: {ex.StackTrace}"
            : $"{additionalMessage}\nException: {ex.Message}\nStackTrace: {ex.StackTrace}";

        WriteLog(message, LogLevel.Error);
    }
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}