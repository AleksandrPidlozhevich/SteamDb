using System;
using System.Collections.Generic;

namespace SteamDb.Services;

/// <summary>
/// Stand-ins for the Avalonia previewer (the design-time view model constructors): the IDE runs
/// several previewer hosts, and they must not read or write the user's encrypted settings or log.
/// </summary>
internal sealed class DesignTimeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Load(string key) => _values.GetValueOrDefault(key);

    public void Save(string key, string value) => _values[key] = value;

    public void Delete(string key) => _values.Remove(key);
}

/// <inheritdoc cref="DesignTimeSecretStore"/>
internal sealed class DesignTimeLog : ILogService
{
    public void WriteLog(string message, LogLevel level = LogLevel.Info)
    {
    }

    public void WriteInfo(string message)
    {
    }

    public void WriteWarning(string message)
    {
    }

    public void WriteError(string message)
    {
    }

    public void WriteException(Exception ex, string additionalMessage = "")
    {
    }
}
