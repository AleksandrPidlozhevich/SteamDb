using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SteamDb.Models;
using SteamDb.Services;
using Xunit;

namespace SteamDb.Tests;

public class NotionGameExporterTests
{
    [Fact]
    public async Task Export_NewGameNotInDatabase_CreatesOneAndUpdatesNone()
    {
        var incoming = CsvGameExportService.CreateRow("Steam", "New Game", "Steam:1");
        var result = await RunExport(Library(incoming), existing: []);

        Assert.Single(result.Created);
        Assert.Empty(result.Updated);
    }

    [Fact]
    public async Task Export_IncomingAddsPlatformToExistingRow_UpdatesItNotCreates()
    {
        // Existing Steam-only row; incoming GOG row for the same title (matched by name) adds GOG,
        // so the stored Notion page must be updated rather than a duplicate created.
        var existing = Existing("p1",
            CsvGameExportService.CreateRow("Steam", "The Witcher 3", "Steam:292030"),
            gameId: "Steam:292030", platforms: "Steam");
        var incoming = CsvGameExportService.CreateRow("GOG", "The Witcher 3", "GOG:99");

        var result = await RunExport(Library(incoming), [existing]);

        Assert.Empty(result.Created);
        var update = Assert.Single(result.Updated);
        Assert.Equal("p1", update.PageId);
    }

    [Fact]
    public async Task Export_EverythingAlreadyUpToDate_DoesNothing()
    {
        var existing = Existing("p1",
            CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400"),
            gameId: "Steam:400", platforms: "Steam");
        var incoming = CsvGameExportService.CreateRow("Steam", "Portal", "Steam:400");

        var result = await RunExport(Library(incoming), [existing]);

        Assert.Empty(result.Created);
        Assert.Empty(result.Updated);
    }

    [Fact]
    public async Task Export_LegacyBareId_IsNormalizedToPrefixedForm_EvenWithoutIncoming()
    {
        // Stored GameID is a legacy bare number while the row resolves to "Steam:379720"; the
        // export should rewrite it to the prefixed form even though the game isn't in the import.
        var existing = Existing("p1",
            CsvGameExportService.CreateRow("Steam", "Doom", "Steam:379720"),
            gameId: "379720", platforms: "Steam");

        var result = await RunExport(Library(/* nothing owned */), [existing]);

        Assert.Empty(result.Created);
        Assert.Single(result.Updated);
    }

    [Fact]
    public async Task Export_DisposesTheNotionClient()
    {
        var result = await RunExport(Library(), existing: []);
        Assert.True(result.Disposed); // the `using` in ExportAsync released the HttpClient-owning client
    }

    // ---- helpers ---------------------------------------------------------------------

    private static async Task<(List<object> Created, List<(string PageId, object Properties)> Updated, bool Disposed)>
        RunExport(GameLibraryResult library, IReadOnlyList<NotionGameRow> existing)
    {
        var client = new FakeNotionApiClient();
        var fetcher = new FakeNotionDataFetcher(existing.ToList());
        var exporter = new NotionGameExporter(
            new FakeGameLibraryService(library),
            new FakeNotionGateway(client, fetcher),
            new NullLogService());

        await exporter.ExportAsync("key", "id", "token", "db");
        return (client.Created, client.Updated, client.Disposed);
    }

    private static GameLibraryResult Library(params CsvGameExportRow[] rows) =>
        new(rows.ToList(), false, false, false, false, false, false);

    private static NotionGameRow Existing(string pageId, CsvGameExportRow row, string gameId, params string[] platforms) =>
        new(row, pageId, new HashSet<string>(platforms, StringComparer.OrdinalIgnoreCase), gameId);
}

// ---- fakes -----------------------------------------------------------------------------

file sealed class FakeGameLibraryService(GameLibraryResult result) : IGameLibraryService
{
    public Task<GameLibraryResult> FetchAsync(
        string? steamApiKey, string? steamId,
        IProgress<StoreFetchProgress>? progress = null, Action<string>? onStatus = null,
        CancellationToken ct = default) => Task.FromResult(result);
}

file sealed class FakeNotionGateway(INotionApiClient client, INotionDataFetcher fetcher) : INotionGateway
{
    public INotionApiClient CreateClient(string? token, string? dbId) => client;
    public INotionDataFetcher CreateFetcher(INotionApiClient c) => fetcher;
}

file sealed class FakeNotionDataFetcher(List<NotionGameRow> rows) : INotionDataFetcher
{
    public Task<List<NotionGameRow>> FetchRowsAsync(CancellationToken ct = default) => Task.FromResult(rows);
}

file sealed class FakeNotionApiClient : INotionApiClient
{
    public List<object> Created { get; } = [];
    public List<(string PageId, object Properties)> Updated { get; } = [];
    public bool Disposed { get; private set; }

    public Task<List<JObject>> QueryAllPagesAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<JObject>());

    public Task AddPagesToDatabaseParallel(
        IEnumerable<object> pages, Action? onPageDone = null, CancellationToken ct = default)
    {
        foreach (var page in pages)
        {
            Created.Add(page);
            onPageDone?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task UpdatePagesParallel(
        IEnumerable<(string PageId, object Properties)> updates,
        Action? onPageDone = null, CancellationToken ct = default)
    {
        foreach (var update in updates)
        {
            Updated.Add(update);
            onPageDone?.Invoke();
        }

        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}

file sealed class NullLogService : ILogService
{
    public void WriteLog(string message, LogLevel level = LogLevel.Info) { }
    public void WriteInfo(string message) { }
    public void WriteWarning(string message) { }
    public void WriteError(string message) { }
    public void WriteException(Exception ex, string additionalMessage = "") { }
}
