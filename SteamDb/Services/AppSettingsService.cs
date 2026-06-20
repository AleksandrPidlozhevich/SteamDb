using System;

namespace SteamDb.Services;

/// <summary>Credentials persisted to / loaded from the plaintext settings file.</summary>
public sealed record AppSettings(string? SteamApiKey, string? SteamId, string? NotionToken, string? DbId);

public static class AppSettingsService
{
    public static string Serialize(AppSettings settings)
    {
        return $"Steam API Key: {settings.SteamApiKey}" + Environment.NewLine +
               $"Steam ID: {settings.SteamId}" + Environment.NewLine +
               $"NotionToken: {settings.NotionToken}" + Environment.NewLine +
               $"DbId: {settings.DbId}" + Environment.NewLine;
    }

    /// <summary>
    /// Parses the settings file. Fields absent from the file are returned as null so the
    /// caller can leave the corresponding values untouched.
    /// </summary>
    public static AppSettings Parse(string content)
    {
        string? steamApiKey = null, steamId = null, notionToken = null, dbId = null;

        foreach (var line in content.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var keyValue = line.Split(": ", 2);
            if (keyValue.Length != 2) continue;

            switch (keyValue[0])
            {
                case "Steam API Key": steamApiKey = keyValue[1]; break;
                case "Steam ID": steamId = keyValue[1]; break;
                case "NotionToken": notionToken = keyValue[1]; break;
                case "DbId": dbId = keyValue[1]; break;
            }
        }

        return new AppSettings(steamApiKey, steamId, notionToken, dbId);
    }
}
