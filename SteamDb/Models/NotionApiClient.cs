using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Models;

public class NotionApiClient : INotionApiClient
{
    private const int DelayBetweenRequests = 334;
    private const int MaxConcurrentRequests = 3;

    // Notion API version. As of 2025-09-03 a database and its data source(s) are distinct: rows are
    // queried and pages are created against a data source, not the database directly.
    private const string NotionVersion = "2025-09-03";

    private readonly string _apiKey;
    private readonly string _databaseId;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _semaphore;

    // The database's (first) data source id, resolved lazily from the database id and cached.
    private string? _dataSourceId;

    public NotionApiClient(string? apiKey, string? databaseId)
    {
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _databaseId = databaseId?.Trim() ?? string.Empty;
        _httpClient = new HttpClient();
        _semaphore = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);
        InitializeHttpClient();
    }

    private void InitializeHttpClient()
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("Notion-Version", NotionVersion);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    // Resolves the data source id behind the configured database id (single-source databases expose
    // exactly one). Cached after the first call so the export only makes this round-trip once.
    private async Task<string> ResolveDataSourceIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_dataSourceId)) return _dataSourceId;

        var response = await _httpClient.GetAsync($"https://api.notion.com/v1/databases/{_databaseId}", ct);
        await EnsureNotionSuccessAsync(response, ct);

        var database = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
        var id = (database["data_sources"] as JArray)?.FirstOrDefault()?["id"]?.Value<string>();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException($"Notion database {_databaseId} exposes no data source.");

        _dataSourceId = id;
        return id;
    }

    // EnsureSuccessStatusCode hides Notion's response body, which carries the actual reason
    // (e.g. an invalid token or an unshared database). Surface it so failures are diagnosable.
    private static async Task EnsureNotionSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        string? notionMessage = null;
        try
        {
            notionMessage = JObject.Parse(body)["message"]?.Value<string>();
        }
        catch (JsonReaderException)
        {
            // Non-JSON body (e.g. a gateway error page) — fall back to the raw content below.
        }

        var detail = notionMessage ?? body;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            detail = $"Notion rejected the integration token (401). " +
                     $"Check the token in settings and that the database is shared with the integration. {detail}";

        throw new HttpRequestException($"Notion API {(int)response.StatusCode} {response.StatusCode}: {detail}");
    }

    public async Task<List<JObject>> QueryAllPagesAsync(CancellationToken ct = default)
    {
        var dataSourceId = await ResolveDataSourceIdAsync(ct);
        var allPages = new List<JObject>();
        string? nextCursor = null;

        do
        {
            object queryPayload;
            if (nextCursor != null)
                queryPayload = new { start_cursor = nextCursor };
            else
                queryPayload = new { };

            var queryUrl = $"https://api.notion.com/v1/data_sources/{dataSourceId}/query";
            var response = await _httpClient.PostAsync(
                queryUrl,
                new StringContent(JsonConvert.SerializeObject(queryPayload), Encoding.UTF8, "application/json"),
                ct
            );

            await EnsureNotionSuccessAsync(response, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);
            var result = JObject.Parse(responseContent);

            var pages = result["results"] as JArray;
            if (pages != null)
                foreach (var page in pages)
                    if (page is JObject pageObject)
                        allPages.Add(pageObject);

            var hasMore = result["has_more"]?.Value<bool>() ?? false;
            nextCursor = hasMore ? result["next_cursor"]?.Value<string>() : null;

            if (nextCursor != null) await Task.Delay(DelayBetweenRequests, ct);
        } while (nextCursor != null);

        return allPages;
    }

    public async Task AddPagesToDatabaseParallel(
        IEnumerable<object> pageProperties, Action? onPageDone = null, CancellationToken ct = default)
    {
        var dataSourceId = await ResolveDataSourceIdAsync(ct);
        var tasks = pageProperties
            .Select(properties => AddPageWithThrottling(properties, dataSourceId, onPageDone, ct))
            .ToList();
        await Task.WhenAll(tasks);
    }

    private async Task AddPageWithThrottling(
        object properties, string dataSourceId, Action? onPageDone, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await AddPageToDatabaseWithRetry(properties, dataSourceId, ct);
            onPageDone?.Invoke();
        }
        finally
        {
            _semaphore.Release();
            await Task.Delay(DelayBetweenRequests, ct);
        }
    }

    private async Task AddPageToDatabaseWithRetry(
        object properties, string dataSourceId, CancellationToken ct, int maxRetries = 3)
    {
        var newPageData = new
        {
            parent = new { type = "data_source_id", data_source_id = dataSourceId },
            properties
        };

        for (var retry = 0; retry <= maxRetries; retry++)
            try
            {
                var response = await _httpClient.PostAsync(
                    "https://api.notion.com/v1/pages",
                    new StringContent(JsonConvert.SerializeObject(newPageData), Encoding.UTF8, "application/json"),
                    ct
                );

                if (response.IsSuccessStatusCode)
                    return;

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, retry));
                    await Task.Delay(retryAfter, ct);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Status: {response.StatusCode}, Content: {content}");
            }
            catch (HttpRequestException) when (retry < maxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), ct);
            }
    }

    public async Task UpdatePagesParallel(
        IEnumerable<(string PageId, object Properties)> updates,
        Action? onPageDone = null,
        CancellationToken ct = default)
    {
        var tasks = updates.Select(u => UpdatePageWithThrottling(u.PageId, u.Properties, onPageDone, ct)).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task UpdatePageWithThrottling(
        string pageId, object properties, Action? onPageDone, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await UpdatePageWithRetry(pageId, properties, ct);
            onPageDone?.Invoke();
        }
        finally
        {
            _semaphore.Release();
            await Task.Delay(DelayBetweenRequests, ct);
        }
    }

    private async Task UpdatePageWithRetry(string pageId, object properties, CancellationToken ct, int maxRetries = 3)
    {
        for (var retry = 0; retry <= maxRetries; retry++)
            try
            {
                using var request =
                    new HttpRequestMessage(HttpMethod.Patch, $"https://api.notion.com/v1/pages/{pageId}")
                    {
                        Content = new StringContent(
                            JsonConvert.SerializeObject(new { properties }), Encoding.UTF8, "application/json")
                    };
                var response = await _httpClient.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                    return;

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, retry));
                    await Task.Delay(retryAfter, ct);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Status: {response.StatusCode}, Content: {content}");
            }
            catch (HttpRequestException) when (retry < maxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), ct);
            }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _semaphore?.Dispose();
    }
}