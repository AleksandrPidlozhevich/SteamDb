using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>
/// Keeps the previous version of a CSV before Update .csv (or <c>--sync csv</c>) rewrites it:
/// <c>games.csv</c> → <c>games.csv.bak</c>, replaced on every update. To undo an update, delete the
/// CSV and rename the .bak back.
/// </summary>
public static class CsvBackup
{
    public static string PathFor(string csvPath) => csvPath + ".bak";

    /// <summary>Writes <paramref name="previousContent"/> to the backup file; returns its path, or null
    /// when there's nothing to back up (no local path, or an empty / new file).</summary>
    public static async Task<string?> WriteAsync(string? csvPath, string? previousContent)
    {
        if (string.IsNullOrEmpty(csvPath) || string.IsNullOrWhiteSpace(previousContent)) return null;

        var backup = PathFor(csvPath);
        // Same encoding as the CSV itself (UTF-8 with BOM, for Excel).
        await File.WriteAllTextAsync(backup, previousContent, new UTF8Encoding(true));
        return backup;
    }
}
