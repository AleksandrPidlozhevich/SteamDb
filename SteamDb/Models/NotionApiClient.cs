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

public class NotionApiClient
{
    private const int DelayBetweenRequests = 334;
    private const int MaxConcurrentRequests = 3;
    private readonly string _apiKey;
    private readonly string _databaseId;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _semaphore;

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
        _httpClient.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<string> QueryDatabaseAsync()
    {
        var queryPayload = new { };
        var queryUrl = $"https://api.notion.com/v1/databases/{_databaseId}/query";
        var response = await _httpClient.PostAsync(
            queryUrl,
            new StringContent(JsonConvert.SerializeObject(queryPayload), Encoding.UTF8, "application/json")
        );

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<List<JObject>> QueryAllPagesAsync(CancellationToken ct = default)
    {
        var allPages = new List<JObject>();
        string? nextCursor = null;

        do
        {
            object queryPayload;
            if (nextCursor != null)
                queryPayload = new { start_cursor = nextCursor };
            else
                queryPayload = new { };

            var queryUrl = $"https://api.notion.com/v1/databases/{_databaseId}/query";
            var response = await _httpClient.PostAsync(
                queryUrl,
                new StringContent(JsonConvert.SerializeObject(queryPayload), Encoding.UTF8, "application/json"),
                ct
            );

            response.EnsureSuccessStatusCode();
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
        IEnumerable<object> pages, Action? onPageDone = null, CancellationToken ct = default)
    {
        var tasks = pages.Select(page => AddPageWithThrottling(page, onPageDone, ct)).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task AddPageWithThrottling(object page, Action? onPageDone, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await AddPageToDatabaseWithRetry(page, ct);
            onPageDone?.Invoke();
        }
        finally
        {
            _semaphore.Release();
            await Task.Delay(DelayBetweenRequests, ct);
        }
    }

    private async Task AddPageToDatabaseWithRetry(object newPageData, CancellationToken ct, int maxRetries = 3)
    {
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

    public async Task AddPagesToDatabase(IEnumerable<object> pages)
    {
        foreach (var page in pages)
            try
            {
                await AddPageToDatabaseAsync(page);
                await Task.Delay(DelayBetweenRequests);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429"))
            {
                await Task.Delay(2000);
                await AddPageToDatabaseAsync(page);
            }
    }

    public async Task AddPageToDatabaseAsync(object newPageData)
    {
        var response = await _httpClient.PostAsync(
            "https://api.notion.com/v1/pages",
            new StringContent(JsonConvert.SerializeObject(newPageData), Encoding.UTF8, "application/json")
        );

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Status: {response.StatusCode}, Content: {content}");
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _semaphore?.Dispose();
    }
}