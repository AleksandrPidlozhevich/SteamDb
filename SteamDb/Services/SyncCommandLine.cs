using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamDb.Services;

/// <summary>Export targets the command-line sync can run.</summary>
public enum SyncTarget
{
    Csv,
    Notion,
    Sheets
}

/// <summary>A parsed <c>--sync</c> invocation.</summary>
public sealed record SyncOptions(
    IReadOnlyList<SyncTarget> Targets,
    string? CsvFile,
    string? SheetName,
    bool DryRun);

/// <summary>Result of <see cref="SyncCommandLine.Parse"/>: options, or the text to print and exit with.</summary>
public sealed record SyncCommand(SyncOptions? Options, string? Message, int ExitCode)
{
    public static SyncCommand Error(string message) => new(null, message + "\n\n" + SyncCommandLine.Usage, 2);
}

/// <summary>
/// Parses the headless sync command line, e.g. <c>SteamDb --sync notion,sheets --sheet "My games"</c>.
/// Pure, so it's unit-tested; <see cref="HeadlessSync"/> runs the result.
/// </summary>
public static class SyncCommandLine
{
    public const string Usage =
        """
        Usage: SteamDb --sync <targets> [options]

          <targets>        Comma-separated: csv, notion, sheets
          --file <path>    CSV file to update (created if missing). Default: the CSV last
                           exported / updated in the app.
          --sheet <name>   Google Sheets table name (default: the one saved in the app).
          --dry-run        Show what would change without writing anything.
          --help           Show this help.

        Credentials and store logins come from the app: open SteamDb once, fill in the fields
        and connect your stores. Exit code: 0 = success, 1 = a sync failed, 2 = bad arguments.
        """;

    /// <summary>True when the app should run headless instead of opening its window.</summary>
    public static bool IsHeadless(IReadOnlyList<string> args) =>
        args.Any(a => a is "--sync" or "--help" or "-h" or "/?");

    public static SyncCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
            return new SyncCommand(null, Usage, 0);

        var targets = new List<SyncTarget>();
        string? file = null, sheet = null;
        var dryRun = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--sync":
                    if (++i >= args.Count) return SyncCommand.Error("--sync needs a target: csv, notion or sheets.");
                    foreach (var name in args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        SyncTarget? target = name.ToLowerInvariant() switch
                        {
                            "csv" => SyncTarget.Csv,
                            "notion" => SyncTarget.Notion,
                            "sheets" or "google-sheets" => SyncTarget.Sheets,
                            _ => null
                        };
                        if (target == null) return SyncCommand.Error($"Unknown sync target \"{name}\".");
                        if (!targets.Contains(target.Value)) targets.Add(target.Value);
                    }

                    break;
                case "--file":
                    if (++i >= args.Count) return SyncCommand.Error("--file needs a path.");
                    file = args[i];
                    break;
                case "--sheet":
                    if (++i >= args.Count) return SyncCommand.Error("--sheet needs a table name.");
                    sheet = args[i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    return SyncCommand.Error($"Unknown argument \"{args[i]}\".");
            }
        }

        if (targets.Count == 0) return SyncCommand.Error("Nothing to sync: pass --sync csv, notion or sheets.");

        return new SyncCommand(new SyncOptions(targets, file, sheet, dryRun), null, 0);
    }
}
