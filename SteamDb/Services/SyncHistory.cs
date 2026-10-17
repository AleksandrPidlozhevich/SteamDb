using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SteamDb.Services;

/// <summary>
/// Remembers when each export target was last synced successfully (shown under its button).
/// Kept in the encrypted secret store next to the settings: not secret, but it avoids another file.
/// Format: one <c>Target=ISO-8601</c> line per target.
/// </summary>
public sealed class SyncHistory
{
    public const string Csv = "CSV";
    public const string Notion = "Notion";
    public const string GoogleSheets = "Google Sheets";

    private const string SecretKey = "last-sync";

    private readonly ISecretStore _secrets;
    private readonly Dictionary<string, DateTimeOffset> _entries;

    public SyncHistory(ISecretStore secrets)
    {
        _secrets = secrets;
        _entries = Parse(secrets.Load(SecretKey));
    }

    public DateTimeOffset? Get(string target) => _entries.TryGetValue(target, out var when) ? when : null;

    public void Record(string target, DateTimeOffset when)
    {
        _entries[target] = when;
        _secrets.Save(SecretKey, string.Join("\n",
            _entries.Select(e => $"{e.Key}={e.Value.ToString("O", CultureInfo.InvariantCulture)}")));
    }

    /// <summary>"Last sync: today 14:20", "… yesterday 09:05", "… 3 Oct 18:40"; null when never.</summary>
    public static string? Describe(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when == null) return null;

        var local = when.Value.ToOffset(now.Offset);
        var days = (now.Date - local.Date).Days;
        var day = days switch
        {
            0 => "today",
            1 => "yesterday",
            _ => local.ToString(local.Year == now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture)
        };
        return $"Last sync: {day} {local:HH:mm}";
    }

    private static Dictionary<string, DateTimeOffset> Parse(string? content)
    {
        var entries = new Dictionary<string, DateTimeOffset>();
        if (string.IsNullOrEmpty(content)) return entries;

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.LastIndexOf('=');
            if (separator > 0 && DateTimeOffset.TryParse(line[(separator + 1)..].Trim(),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                entries[line[..separator]] = when;
        }

        return entries;
    }
}
