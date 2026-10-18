using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

/// <summary>
/// The in-app auto-sync choice: how often (hours, 0 = off) and which targets. Saved in the remembered
/// settings as <c>Auto Sync: 24h Notion, Sheets, CSV</c>. CSV updates the file last written by
/// Export / Update .csv (remembered as <c>CSV File</c>).
/// </summary>
public sealed record AutoSyncSettings(int IntervalHours, IReadOnlyList<SyncTarget> Targets)
{
    /// <summary>The intervals offered in the UI, in hours (index = combo box position).</summary>
    public static readonly IReadOnlyList<int> Intervals = [0, 1, 6, 24];

    public static readonly IReadOnlyList<string> IntervalLabels = ["Off", "Hourly", "Every 6h", "Daily"];

    public static AutoSyncSettings Off { get; } = new(0, []);

    public bool IsOn => IntervalHours > 0 && Targets.Count > 0;

    /// <summary>The settings-file value, or null when off (so the line is omitted).</summary>
    public string? Serialize() =>
        IsOn ? $"{IntervalHours}h {string.Join(", ", Targets.Select(Name))}" : null;

    public static AutoSyncSettings Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Off;

        var parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].EndsWith('h') ||
            !int.TryParse(parts[0][..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !Intervals.Contains(hours) || hours == 0)
            return Off;

        var targets = new List<SyncTarget>();
        foreach (var name in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            SyncTarget? target = name.ToLowerInvariant() switch
            {
                "notion" => SyncTarget.Notion,
                "sheets" => SyncTarget.Sheets,
                "csv" => SyncTarget.Csv,
                _ => null
            };
            if (target is { } t && !targets.Contains(t)) targets.Add(t);
        }

        return targets.Count == 0 ? Off : new AutoSyncSettings(hours, targets);
    }

    private static string Name(SyncTarget target) => target switch
    {
        SyncTarget.Notion => "Notion",
        SyncTarget.Sheets => "Sheets",
        _ => "CSV"
    };
}

/// <summary>Decides which auto-sync targets are due. Pure, so it's unit-tested.</summary>
public static class AutoSyncPlanner
{
    /// <summary>After a failed auto-sync, wait this long before trying that target again.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <param name="lastSuccess">When the target last synced (manual, auto or <c>--sync</c>), or null.</param>
    /// <param name="lastFailure">When an auto-sync of the target last failed in this session.</param>
    /// <param name="canRun">Whether the target has what it needs (credentials / table name).</param>
    public static IReadOnlyList<SyncTarget> Due(
        AutoSyncSettings settings,
        Func<SyncTarget, DateTimeOffset?> lastSuccess,
        IReadOnlyDictionary<SyncTarget, DateTimeOffset> lastFailure,
        Func<SyncTarget, bool> canRun,
        DateTimeOffset now)
    {
        if (!settings.IsOn) return [];

        var interval = TimeSpan.FromHours(settings.IntervalHours);
        return settings.Targets
            .Where(canRun)
            .Where(t => lastSuccess(t) is not { } last || now - last >= interval)
            .Where(t => !lastFailure.TryGetValue(t, out var failed) || now - failed >= RetryAfterFailure)
            .ToList();
    }
}
