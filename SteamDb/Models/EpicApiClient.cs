using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamDb.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Models;

/// <summary>Progress of a store's library/catalog fetch. <paramref name="Stage"/> labels the
/// step for the UI (e.g. "Loading Epic catalog", "GOG library").</summary>
public readonly record struct StoreFetchProgress(int Completed, int Total, string Stage = "");

/// <summary>Outcome of trying to authenticate a store from its cached refresh token.</summary>
public enum StoreAuthFromCacheStatus
{
    /// <summary>No cached session exists — the user has never connected.</summary>
    NoCachedSession,

    /// <summary>The cached refresh token was accepted.</summary>
    Authenticated,

    /// <summary>A cached token existed but is no longer valid (expired/revoked).</summary>
    SessionExpired
}

public class EpicApiClient : IStoreClient
{
    private const string LauncherClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string LauncherClientSecret = "daafbccc737745039dffe53d94fc76cf";

    private const string OAuthTokenUrl =
        "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token";

    private const string AssetsUrl =
        "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/Windows?label=Live";

    // The bulk endpoint is per-namespace and accepts many ids per request, so we batch
    // ids by namespace instead of issuing one request per game.
    private const string CatalogBulkUrlTemplate =
        "https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/{0}/bulk/items";

    private const string CatalogBulkQuerySuffix =
        "&includeMainGameDetails=true&country=US&locale=en-US";

    private const string AuthorizationCodeRedirectUrl =
        "https://www.epicgames.com/id/api/redirect?clientId=" + LauncherClientId + "&responseType=code";

    private const string LoginUrl =
        "https://www.epicgames.com/id/login?redirectUrl=";

    // Catalog requests are batched and throttled to stay under Epic's rate limits.
    private const int CatalogMaxConcurrency = 8;
    private const int CatalogBatchSize = 50;
    private const int CatalogMaxRetries = 4;

    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
        return client;
    }

    private readonly string _tokenCachePath;

    // Serialises access-token refreshes so concurrent catalog requests that all hit a
    // 401 don't fire several refreshes at once (Epic rotates the refresh token each time).
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private string? _accountId;

    public EpicApiClient(string? tokenStorageFolder = null)
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var folder = tokenStorageFolder ?? Path.Combine(exeDir, "EpicTokenStorage");
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        _tokenCachePath = Path.Combine(folder, "epic_token.json");
    }

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

    public async Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync()
    {
        var rt = LoadRefreshTokenFromCache();
        if (string.IsNullOrEmpty(rt)) return StoreAuthFromCacheStatus.NoCachedSession;

        try
        {
            // On a 4xx (invalid/expired token) RequestTokenAsync clears the cache for us.
            return await AuthenticateWithRefreshTokenAsync(rt)
                ? StoreAuthFromCacheStatus.Authenticated
                : StoreAuthFromCacheStatus.SessionExpired;
        }
        catch (Exception ex)
        {
            LogService.WriteWarning($"Epic: refresh token invalid, re-login required. {ex.Message}");
            return StoreAuthFromCacheStatus.SessionExpired;
        }
    }

    public void OpenLoginPageInBrowser()
    {
        var redirect = Uri.EscapeDataString(AuthorizationCodeRedirectUrl);
        OpenUrl(LoginUrl + redirect);
        LogService.WriteInfo("Epic: opened login page in system browser.");
    }

    public async Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new ArgumentException("Authorization code is empty", nameof(authorizationCode));

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode.Trim(),
            ["token_type"] = "eg1"
        };

        return await RequestTokenAsync(form);
    }

    public async Task<bool> AuthenticateWithRefreshTokenAsync(string refreshToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["token_type"] = "eg1"
        };

        return await RequestTokenAsync(form);
    }

    private async Task<bool> RequestTokenAsync(Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl);
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{LauncherClientId}:{LauncherClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(form);

        var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            LogService.WriteError($"Epic token request failed: {response.StatusCode}, {body}");

            // A refresh-token grant rejected with a client error means the cached token
            // is dead — drop it so we don't keep retrying a token that can't work.
            if (form.TryGetValue("grant_type", out var grant) && grant == "refresh_token" &&
                (int)response.StatusCode is >= 400 and < 500)
            {
                LogService.WriteWarning("Epic: cached refresh token rejected — clearing token cache.");
                DeleteTokenCache();
            }

            return false;
        }

        var json = JObject.Parse(body);
        _accessToken = json["access_token"]?.Value<string>();
        _refreshToken = json["refresh_token"]?.Value<string>();
        _accountId = json["account_id"]?.Value<string>();

        if (string.IsNullOrEmpty(_accessToken))
        {
            LogService.WriteError("Epic token response has no access_token.");
            return false;
        }

        SaveRefreshTokenToCache(_refreshToken);
        LogService.WriteInfo($"Epic: authenticated as account {_accountId}.");
        return true;
    }

    public async Task<List<EpicGame>> GetOwnedGamesAsync(IProgress<StoreFetchProgress>? progress = null)
    {
        EnsureAuthenticated();

        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, AssetsUrl));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var assets = JsonConvert.DeserializeObject<List<EpicGame>>(body) ?? new List<EpicGame>();

        var games = assets
            .Where(a => !string.IsNullOrEmpty(a.CatalogItemId) && !string.IsNullOrEmpty(a.Namespace))
            .GroupBy(a => a.CatalogItemId)
            .Select(g => g.First())
            .ToList();

        await EnrichTitlesAsync(games, progress);

        LogService.WriteInfo($"Epic: fetched {games.Count} owned games.");
        return games;
    }

    private async Task EnrichTitlesAsync(List<EpicGame> games, IProgress<StoreFetchProgress>? progress)
    {
        var total = games.Count;
        var completed = 0;
        progress?.Report(new StoreFetchProgress(0, total, "Loading Epic catalog"));

        // Original position per game, so output order is stable regardless of which
        // batch finishes first (CatalogItemId is unique — games were deduped on it).
        var orderByCatalogId = new Dictionary<string, int>();
        for (var i = 0; i < games.Count; i++)
            orderByCatalogId[games[i].CatalogItemId] = i;

        var batches = BuildCatalogBatches(games);
        using var semaphore = new SemaphoreSlim(CatalogMaxConcurrency, CatalogMaxConcurrency);
        var ownedGames = new ConcurrentBag<(int Order, EpicGame Game)>();

        var tasks = batches.Select(async batch =>
        {
            await semaphore.WaitAsync();
            try
            {
                var catalog = await FetchCatalogBatchAsync(
                    batch.Namespace,
                    batch.Games.Select(g => g.CatalogItemId).ToList());

                foreach (var game in batch.Games)
                {
                    var item = catalog?[game.CatalogItemId];
                    if (item == null) continue;

                    if (!IsBaseGameCatalogItem(item))
                    {
                        LogService.WriteInfo($"Epic: skipped non-game catalog item {game.CatalogItemId} ({game.AppName}).");
                        continue;
                    }

                    game.Title = item["title"]?.Value<string>() ?? game.AppName;
                    ownedGames.Add((orderByCatalogId[game.CatalogItemId], game));
                }
            }
            catch (Exception ex)
            {
                LogService.WriteWarning($"Epic: catalog batch failed for namespace {batch.Namespace}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
                progress?.Report(new StoreFetchProgress(
                    Interlocked.Add(ref completed, batch.Games.Count), total, "Loading Epic catalog"));
            }
        });

        await Task.WhenAll(tasks);

        games.Clear();
        games.AddRange(ownedGames.OrderBy(x => x.Order).Select(x => x.Game));
    }

    // Groups games by namespace, then splits each namespace into chunks small enough
    // to keep the request URL within sane limits.
    private static List<(string Namespace, List<EpicGame> Games)> BuildCatalogBatches(List<EpicGame> games)
    {
        return games
            .GroupBy(g => g.Namespace)
            .SelectMany(nsGroup => nsGroup
                .Select((game, index) => (game, index))
                .GroupBy(x => x.index / CatalogBatchSize)
                .Select(chunk => (Namespace: nsGroup.Key, Games: chunk.Select(x => x.game).ToList())))
            .ToList();
    }

    private async Task<JObject?> FetchCatalogBatchAsync(string ns, List<string> catalogItemIds)
    {
        var query = string.Join("&", catalogItemIds.Select(id => $"id={Uri.EscapeDataString(id)}"));
        var url = string.Format(CatalogBulkUrlTemplate, Uri.EscapeDataString(ns)) +
                  "?" + query + CatalogBulkQuerySuffix;

        for (var attempt = 0; attempt < CatalogMaxRetries; attempt++)
        {
            using var response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url));

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta
                            ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                LogService.WriteWarning(
                    $"Epic: catalog 429 for namespace {ns}, retrying in {delay.TotalSeconds:0.#}s.");
                await Task.Delay(delay);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogService.WriteWarning($"Epic: catalog request failed for namespace {ns}: {response.StatusCode}.");
                return null;
            }

            return JObject.Parse(await response.Content.ReadAsStringAsync());
        }

        LogService.WriteWarning($"Epic: catalog batch gave up after {CatalogMaxRetries} retries for namespace {ns}.");
        return null;
    }

    private static bool IsBaseGameCatalogItem(JToken? item)
    {
        if (item == null) return false;

        var categories = item["categories"]?.Children().ToList() ?? new List<JToken>();

        // Base games live under the "games" category (e.g. "games", "games/edition/base").
        // Note: games are ALSO categorised as "applications", so the presence of an
        // "applications"/"software"/"editors" category must NOT disqualify them.
        var isGame = categories.Any(c => CategoryPathStartsWith(c, "games"));
        if (!isGame) return false;

        // Exclude DLC / add-ons: they sit under "addons" and reference a parent game.
        var isAddon = categories.Any(c => CategoryPathStartsWith(c, "addons"));
        if (isAddon) return false;

        return item["mainGameItem"] == null;
    }

    private static bool CategoryPathStartsWith(JToken category, string prefix)
    {
        var path = category["path"]?.Value<string>();
        return path != null &&
               (string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException("Epic client is not authenticated. Call authenticate first.");
    }

    // Sends a bearer-authorized request; if the access token has expired (401),
    // refreshes it once and retries. The caller owns/dispose the returned response.
    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> requestFactory)
    {
        var staleToken = _accessToken;

        var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await _httpClient.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Unauthorized &&
            await TryRefreshAccessTokenAsync(staleToken))
        {
            response.Dispose();
            request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            response = await _httpClient.SendAsync(request);
        }

        return response;
    }

    private async Task<bool> TryRefreshAccessTokenAsync(string? staleAccessToken)
    {
        await _refreshLock.WaitAsync();
        try
        {
            // A concurrent request may already have refreshed while we waited on the lock.
            if (!string.IsNullOrEmpty(_accessToken) && _accessToken != staleAccessToken)
                return true;

            var rt = _refreshToken ?? LoadRefreshTokenFromCache();
            if (string.IsNullOrEmpty(rt)) return false;

            try
            {
                return await AuthenticateWithRefreshTokenAsync(rt);
            }
            catch (Exception ex)
            {
                LogService.WriteWarning($"Epic: access-token refresh after 401 failed. {ex.Message}");
                return false;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void SaveRefreshTokenToCache(string? refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;
        var payload = new { refresh_token = refreshToken, account_id = _accountId };
        File.WriteAllText(_tokenCachePath, JsonConvert.SerializeObject(payload));
    }

    private void DeleteTokenCache()
    {
        try
        {
            if (File.Exists(_tokenCachePath)) File.Delete(_tokenCachePath);
        }
        catch (Exception ex)
        {
            LogService.WriteWarning($"Epic: failed to delete token cache: {ex.Message}");
        }
    }

    private string? LoadRefreshTokenFromCache()
    {
        if (!File.Exists(_tokenCachePath)) return null;
        try
        {
            var json = JObject.Parse(File.ReadAllText(_tokenCachePath));
            _accountId = json["account_id"]?.Value<string>();
            return json["refresh_token"]?.Value<string>();
        }
        catch (Exception ex)
        {
            LogService.WriteWarning($"Epic: failed to read token cache: {ex.Message}");
            return null;
        }
    }

    private static void OpenUrl(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", url);
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
    }
}
