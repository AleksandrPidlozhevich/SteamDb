using System;

namespace SteamDb.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>Application logger. The default implementation writes daily files next to the exe.</summary>
public interface ILogService
{
    void WriteLog(string message, LogLevel level = LogLevel.Info);

    void WriteInfo(string message);

    void WriteWarning(string message);

    void WriteError(string message);

    void WriteException(Exception ex, string additionalMessage = "");
}
