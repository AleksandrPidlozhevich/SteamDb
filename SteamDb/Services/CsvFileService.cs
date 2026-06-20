using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>Reads/writes SteamDb CSV files chosen through the storage pickers.</summary>
public static class CsvFileService
{
    public static async Task<string?> ReadAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public static async Task WriteAsync(IStorageFile file, string content)
    {
        await using var stream = await file.OpenWriteAsync();
        // Write a UTF-8 BOM so Excel opens the file as UTF-8 (otherwise it assumes the
        // system ANSI codepage and garbles non-ASCII game names). Notepad is fine either way.
        using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteAsync(content);
    }

    /// <summary>Merges incoming games into the existing CSV content and writes the result.</summary>
    public static async Task WriteMergedAsync(
        IStorageFile file,
        string? existingContent,
        List<CsvGameExportRow> incomingRows)
    {
        var existingRows = CsvGameExportService.Parse(existingContent ?? string.Empty);
        var (mergedRows, added, updated) = CsvGameExportService.Merge(existingRows, incomingRows);
        await WriteAsync(file, CsvGameExportService.Serialize(mergedRows));

        LogService.WriteInfo($"CSV updated: {added} new, {updated} changed, {mergedRows.Count} total rows.");
    }
}