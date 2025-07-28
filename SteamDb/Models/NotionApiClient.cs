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

namespace SteamDb.Models
{
    internal class NotionApiClient
    {
        private const int DelayBetweenRequests = 334;
        private const int MaxConcurrentRequests = 3;
        private readonly string _apiKey;
        private readonly string _databaseId;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _semaphore;

        public NotionApiClient(string apiKey, string databaseId)
        {
            _apiKey = apiKey;
            _databaseId = databaseId;
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

        public async Task<List<JObject>> QueryAllPagesAsync()
        {
            var allPages = new List<JObject>();
            string nextCursor = null;

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
                    new StringContent(JsonConvert.SerializeObject(queryPayload), Encoding.UTF8, "application/json")
                );

                response.EnsureSuccessStatusCode();
                var responseContent = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(responseContent);

                var pages = result["results"] as JArray;
                if (pages != null)
                    foreach (var page in pages)
                        allPages.Add(page as JObject);

                var hasMore = result["has_more"]?.Value<bool>() ?? false;
                nextCursor = hasMore ? result["next_cursor"]?.Value<string>() : null;

                if (nextCursor != null) await Task.Delay(DelayBetweenRequests);
            } while (nextCursor != null);

            return allPages;
        }

        public async Task AddPagesToDatabaseParallel(IEnumerable<object> pages)
        {
            var pagesList = pages.ToList();
            var tasks = new List<Task>();

            foreach (var page in pagesList) tasks.Add(AddPageWithThrottling(page));

            await Task.WhenAll(tasks);
        }

        private async Task AddPageWithThrottling(object page)
        {
            await _semaphore.WaitAsync();
            try
            {
                await AddPageToDatabaseWithRetry(page);
            }
            finally
            {
                _semaphore.Release();
                await Task.Delay(DelayBetweenRequests);
            }
        }

        private async Task AddPageToDatabaseWithRetry(object newPageData, int maxRetries = 3)
        {
            for (var retry = 0; retry <= maxRetries; retry++)
                try
                {
                    var response = await _httpClient.PostAsync(
                        "https://api.notion.com/v1/pages",
                        new StringContent(JsonConvert.SerializeObject(newPageData), Encoding.UTF8, "application/json")
                    );

                    if (response.IsSuccessStatusCode)
                        return;

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, retry));
                        await Task.Delay(retryAfter);
                        continue;
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Status: {response.StatusCode}, Content: {content}");
                }
                catch (HttpRequestException) when (retry < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
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
}