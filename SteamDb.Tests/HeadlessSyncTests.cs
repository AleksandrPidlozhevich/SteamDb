using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamDb.Models;
using SteamDb.Services;
using Xunit;

namespace SteamDb.Tests;

public class HeadlessSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SteamDbTests-" + Guid.NewGuid().ToString("N"));

    public HeadlessSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- command line --------------------------------------------------------------------

    [Fact]
    public void Parse_MultipleTargets_AndOptions()
    {
        var command = SyncCommandLine.Parse(["--sync", "notion, sheets", "--sheet", "My games", "--dry-run"]);

        Assert.Equal(0, command.ExitCode);
        Assert.Equal([SyncTarget.Notion, SyncTarget.Sheets], command.Options!.Targets);
        Assert.Equal("My games", command.Options.SheetName);
        Assert.True(command.Options.DryRun);
    }

    // Arguments joined with '|' (xUnit can't take a string[] as the only inline argument).
    [Theory]
    [InlineData("--sync")]
    [InlineData("--sync|steam")]
    [InlineData("--sync|csv")]
    [InlineData("--sync|notion|--bogus")]
    [InlineData("")]
    public void Parse_BadArguments_ExitCode2WithUsage(string joined)
    {
        var command = SyncCommandLine.Parse(joined.Split('|', StringSplitOptions.RemoveEmptyEntries));

        Assert.Null(command.Options);
        Assert.Equal(2, command.ExitCode);
        Assert.Contains("Usage:", command.Message);
    }

    [Fact]
    public void Help_IsHeadless_ExitCode0()
    {
        Assert.True(SyncCommandLine.IsHeadless(["--help"]));
        Assert.False(SyncCommandLine.IsHeadless([]));
        Assert.Equal(0, SyncCommandLine.Parse(["--help"]).ExitCode);
    }

    // ---- settings --------------------------------------------------------------------------

    [Fact]
    public void Settings_SheetName_RoundTrips_AndIsOmittedWhenEmpty()
    {
        var withSheet = AppSettingsService.Serialize(new AppSettings("k", "i", "t", "d", "My games"));
        Assert.Equal("My games", AppSettingsService.Parse(withSheet).GoogleSheetsTableName);

        var without = AppSettingsService.Serialize(new AppSettings("k", "i", "t", "d"));
        Assert.DoesNotContain("Google Sheets", without);
    }

    // ---- CSV sync ------------------------------------------------------------------------

    [Fact]
    public async Task Csv_NewFile_IsCreated_AndSyncRecorded()
    {
        var (sync, output, secrets) = Create(Steam("Portal", 400));
        var path = Path.Combine(_dir, "games.csv");

        var exit = await sync.RunAsync(Options(path));

        Assert.Equal(0, exit);
        Assert.Contains("Steam:400", await File.ReadAllTextAsync(path));
        Assert.Contains("Added 1 games", output.ToString());
        Assert.NotNull(new SyncHistory(secrets).Get(SyncHistory.Csv));
    }

    [Fact]
    public async Task Csv_ExistingFile_IsMerged()
    {
        var path = Path.Combine(_dir, "games.csv");
        await File.WriteAllTextAsync(path, "Platform,Name,ID\nGOG,Gwent,GOG:9\n");
        var (sync, output, _) = Create(Steam("Portal", 400));

        await sync.RunAsync(Options(path));

        var rows = CsvGameExportService.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(["Gwent", "Portal"], rows.Select(r => r.Name).Order());
        Assert.Contains("(2 total)", output.ToString());

        // The previous version is kept next to it, so the update can be undone.
        Assert.Equal("Platform,Name,ID\nGOG,Gwent,GOG:9\n", await File.ReadAllTextAsync(path + ".bak"));
        Assert.Contains("games.csv.bak", output.ToString());
    }

    [Fact]
    public async Task Csv_NewFile_HasNoBackup()
    {
        var path = Path.Combine(_dir, "fresh.csv");
        var (sync, _, _) = Create(Steam("Portal", 400));

        await sync.RunAsync(Options(path));

        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task Csv_DryRun_WritesNothing_AndRecordsNoSync()
    {
        var (sync, output, secrets) = Create(Steam("Portal", 400));
        var path = Path.Combine(_dir, "games.csv");

        var exit = await sync.RunAsync(Options(path) with { DryRun = true });

        Assert.Equal(0, exit);
        Assert.False(File.Exists(path));
        Assert.Contains("Dry run: would add 1", output.ToString());
        Assert.Null(new SyncHistory(secrets).Get(SyncHistory.Csv));
    }

    [Fact]
    public async Task Csv_NotASteamDbFile_FailsWithExitCode1_AndLeavesItAlone()
    {
        var path = Path.Combine(_dir, "budget.csv");
        await File.WriteAllTextAsync(path, "Item,Amount\nRent,100\n");
        var (sync, output, _) = Create(Steam("Portal", 400));

        var exit = await sync.RunAsync(Options(path));

        Assert.Equal(1, exit);
        Assert.Contains("FAILED", output.ToString());
        Assert.Equal("Item,Amount\nRent,100\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Csv_NoGames_Fails()
    {
        var (sync, _, _) = Create();
        Assert.Equal(1, await sync.RunAsync(Options(Path.Combine(_dir, "games.csv"))));
    }

    // ---- helpers -------------------------------------------------------------------------

    private static SyncOptions Options(string csvPath) => new([SyncTarget.Csv], csvPath, null, false);

    private static CsvGameExportRow Steam(string name, int id) =>
        new() { Name = name, HasSteam = true, SteamGameId = id };

    private static (HeadlessSync Sync, StringWriter Output, MemorySecrets Secrets) Create(params CsvGameExportRow[] rows)
    {
        var secrets = new MemorySecrets();
        var log = new SilentLog();
        var library = new FixedLibrary(new GameLibraryResult(rows.ToList(), false, false, false, false, false, false, true));
        var output = new StringWriter();
        var sync = new HeadlessSync(
            secrets, library,
            new NotionGameExporter(library, new NotionGateway(log), log),
            new GoogleSheetsGameExporter(secrets, library, log),
            log, output);
        return (sync, output, secrets);
    }

    private sealed class FixedLibrary(GameLibraryResult result) : IGameLibraryService
    {
        public Task<GameLibraryResult> FetchAsync(
            string? steamApiKey, string? steamId, IProgress<StoreFetchProgress>? progress = null,
            Action<string>? onStatus = null, CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Load(string key) => _values.GetValueOrDefault(key);
        public void Save(string key, string value) => _values[key] = value;
        public void Delete(string key) => _values.Remove(key);
    }

    private sealed class SilentLog : ILogService
    {
        public void WriteLog(string message, LogLevel level = LogLevel.Info) { }
        public void WriteInfo(string message) { }
        public void WriteWarning(string message) { }
        public void WriteError(string message) { }
        public void WriteException(Exception ex, string additionalMessage = "") { }
    }
}
