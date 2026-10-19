using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

/// <summary>One sync run of one target, as shown in the Sync history window.</summary>
/// <param name="Source">What started it: <see cref="SyncLog.Manual"/>, "Auto-sync", "Sync" (tray) or
/// <see cref="SyncLog.CommandLine"/>.</param>
public sealed record SyncLogEntry(DateTimeOffset When, string Target, bool Ok, string Summary, string Source)
{
    /// <summary>"17 Oct 16:23" (local time).</summary>
    public string WhenText => When.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    public string StatusIcon => Ok ? "✓" : "⚠";
}

/// <summary>
/// The last <see cref="MaxEntries"/> sync runs (manual exports, auto-sync, tray "Sync now" and
/// <c>--sync</c>), newest first, so what happened while the app sat in the tray can be looked up.
/// JSON in the state store (key <c>sync-log</c>), re-read on every access like <see cref="SyncHistory"/>.
/// </summary>
public sealed class SyncLog
{
    public const string Manual = "Manual";
    public const string CommandLine = "Command line";
    public const int MaxEntries = 50;

    private const string StateKey = "sync-log";
    private const int MaxSummaryLength = 500;

    private readonly IStateStore? _store;
    private List<SyncLogEntry> _entries = [];

    public SyncLog(IStateStore? store = null)
    {
        _store = store;
    }

    /// <summary>Raised after an entry is added (on the adding thread).</summary>
    public event Action? Changed;

    public IReadOnlyList<SyncLogEntry> Entries
    {
        get
        {
            Reload();
            return _entries;
        }
    }

    public void Add(SyncLogEntry entry)
    {
        Reload();
        var summary = entry.Summary.Trim();
        if (summary.Length > MaxSummaryLength) summary = summary[..(MaxSummaryLength - 1)] + "…";
        _entries = Trim(_entries.Prepend(entry with { Summary = summary }));
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _entries = [];
        Save();
        Changed?.Invoke();
    }

    /// <summary>Newest first, at most <see cref="MaxEntries"/>.</summary>
    public static List<SyncLogEntry> Trim(IEnumerable<SyncLogEntry> entries) =>
        entries.OrderByDescending(e => e.When).Take(MaxEntries).ToList();

    public static string Serialize(IEnumerable<SyncLogEntry> entries) => JsonConvert.SerializeObject(entries.ToList());

    public static List<SyncLogEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return Trim(JArray.Parse(json)
                .Select(t => t.ToObject<SyncLogEntry>())
                .Where(e => e != null && !string.IsNullOrEmpty(e.Target))
                .Select(e => e! with { Summary = e.Summary ?? "", Source = e.Source ?? "" }));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Reload()
    {
        if (_store != null) _entries = Parse(_store.Load(StateKey));
    }

    private void Save() => _store?.Save(StateKey, Serialize(_entries));
}
