using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamDb.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Models;

/// <summary>
/// Client for the Epic Games launcher OAuth API (authorization-code paste flow). The shared
/// refresh-token bearer plumbing lives in <see cref="RefreshTokenStoreClient"/>; this adds the
/// Epic endpoints, the Basic-auth token POST, and the owned-games + catalog-enrichment fetch.
/// </summary>
public class EpicApiClient : RefreshTokenStoreClient
{
    private const string LauncherClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string LauncherClientSecret = "daafbccc737745039dffe53d94fc76cf";

    private const string OAuthTokenUrl =
        "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token";

    private const string AssetsUrl =
        "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/Windows?label=Live";

    private const string CatalogBulkUrlTemplate =
        "https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/{0}/bulk/items";

    private const string CatalogBulkQuerySuffix =
        "&includeMainGameDetails=true&country=US&locale=en-US";

    private const string AuthorizationCodeRedirectUrl =
        "https://www.epicgames.com/id/api/redirect?clientId=" + LauncherClientId + "&responseType=code";

    private const string LoginUrl =
        "https://www.epicgames.com/id/login?redirectUrl=";

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

    public EpicApiClient(ISecretStore secretStore, ILogService log) : base(secretStore, log)
    {
        MigrateLegacyTokenFile();
    }

    protected override string StoreName => "Epic";
    protected override string SecretKey => "epic";
    protected override HttpClient Http => _httpClient;
    protected override string IdFieldName => "account_id";

    protected override string BrowserLoginUrl => LoginUrl + Uri.EscapeDataString(AuthorizationCodeRedirectUrl);

    public override Uri LoginRedirectUri => new("https://www.epicgames.com/id/api/redirect");

    protected override (string Folder, string FileName)? LegacyTokenPath => ("EpicTokenStorage", "epic_token.json");

    protected override Dictionary<string, string> BuildAuthCodeForm(string authorizationCode)
    {
        return new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["token_type"] = "eg1"
        };
    }

    protected override Dictionary<string, string> BuildRefreshForm(string refreshToken)
    {
        return new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["token_type"] = "eg1"
        };
    }

    protected override Task<HttpResponseMessage> SendTokenRequestAsync(Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl);
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{LauncherClientId}:{LauncherClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(form);

        return Http.SendAsync(request);
    }

    public async Task<List<EpicGame>> GetOwnedGamesAsync(
        IProgress<StoreFetchProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureAuthenticated();

        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, AssetsUrl), ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var assets = JsonConvert.DeserializeObject<List<EpicGame>>(body) ?? new List<EpicGame>();

        var games = assets
            .Where(a => !string.IsNullOrEmpty(a.CatalogItemId) && !string.IsNullOrEmpty(a.Namespace))
            .GroupBy(a => a.CatalogItemId)
            .Select(g => g.First())
            .ToList();

        await EnrichTitlesAsync(games, progress, ct);

        Log.WriteInfo($"Epic: fetched {games.Count} owned games.");
        return games;
    }

    private async Task EnrichTitlesAsync(
        List<EpicGame> games, IProgress<StoreFetchProgress>? progress, CancellationToken ct)
    {
        var total = games.Count;
        var completed = 0;
        progress?.Report(new StoreFetchProgress(0, total, "Loading Epic catalog"));

        var orderByCatalogId = new Dictionary<string, int>();
        for (var i = 0; i < games.Count; i++)
            orderByCatalogId[games[i].CatalogItemId] = i;

        var batches = BuildCatalogBatches(games);
        using var semaphore = new SemaphoreSlim(CatalogMaxConcurrency, CatalogMaxConcurrency);
        var ownedGames = new ConcurrentBag<(int Order, EpicGame Game)>();

        var tasks = batches.Select(async batch =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var catalog = await FetchCatalogBatchAsync(
                    batch.Namespace,
                    batch.Games.Select(g => g.CatalogItemId).ToList(),
                    ct);

                foreach (var game in batch.Games)
                {
                    var item = catalog?[game.CatalogItemId];
                    if (item == null) continue;

                    if (!IsBaseGameCatalogItem(item))
                    {
                        Log.WriteInfo(
                            $"Epic: skipped non-game catalog item {game.CatalogItemId} ({game.AppName}).");
                        continue;
                    }

                    game.Title = item["title"]?.Value<string>() ?? game.AppName;
                    ownedGames.Add((orderByCatalogId[game.CatalogItemId], game));
                }
            }
            catch (Exception ex)
            {
                Log.WriteWarning($"Epic: catalog batch failed for namespace {batch.Namespace}: {ex.Message}");
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

    private async Task<JObject?> FetchCatalogBatchAsync(string ns, List<string> catalogItemIds, CancellationToken ct)
    {
        var query = string.Join("&", catalogItemIds.Select(id => $"id={Uri.EscapeDataString(id)}"));
        var url = string.Format(CatalogBulkUrlTemplate, Uri.EscapeDataString(ns)) +
                  "?" + query + CatalogBulkQuerySuffix;

        for (var attempt = 0; attempt < CatalogMaxRetries; attempt++)
        {
            using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta
                            ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Log.WriteWarning(
                    $"Epic: catalog 429 for namespace {ns}, retrying in {delay.TotalSeconds:0.#}s.");
                await Task.Delay(delay, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.WriteWarning($"Epic: catalog request failed for namespace {ns}: {response.StatusCode}.");
                return null;
            }

            return JObject.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        Log.WriteWarning($"Epic: catalog batch gave up after {CatalogMaxRetries} retries for namespace {ns}.");
        return null;
    }

    private static bool IsBaseGameCatalogItem(JToken? item)
    {
        if (item == null) return false;

        var categories = item["categories"]?.Children().ToList() ?? new List<JToken>();

        var isGame = categories.Any(c => CategoryPathStartsWith(c, "games"));
        if (!isGame) return false;

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
}
