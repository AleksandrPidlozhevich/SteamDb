using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Models;

/// <summary>
/// Minimal client for GOG's (unofficial) Galaxy OAuth API. Mirrors <see cref="EpicApiClient"/>:
/// the user logs in via the browser and pastes the redirect's authorization code; the refresh
/// token is cached and renewed on demand, with a one-shot refresh + retry on HTTP 401.
/// </summary>
public class GogApiClient : IStoreClient
{
    // Well-known GOG Galaxy client credentials (used by Heroic / gogdl / Lutris).
    private const string ClientId = "46899977096215655";
    private const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    private const string RedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    private const string TokenUrl = "https://auth.gog.com/token";

    private const string LoginUrl =
        "https://auth.gog.com/auth?client_id=" + ClientId +
        "&redirect_uri=https%3A%2F%2Fembed.gog.com%2Fon_login_success%3Forigin%3Dclient" +
        "&response_type=code&layout=client2";

    // Owned games with titles, paginated.
    private const string FilteredProductsUrlTemplate =
        "https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={0}";

    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        // GOG rejects requests without a recognised client User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "GOGGalaxyClient/2.0.45 (Windows 10)");
        return client;
    }

    private readonly string _tokenCachePath;

    // Serialises access-token refreshes so concurrent requests that hit a 401 don't fire
    // several refreshes at once (GOG rotates the refresh token on each use).
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private string? _userId;

    public GogApiClient(string? tokenStorageFolder = null)
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var folder = tokenStorageFolder ?? Path.Combine(exeDir, "GogTokenStorage");
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        _tokenCachePath = Path.Combine(folder, "gog_token.json");
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
            LogService.WriteWarning($"GOG: refresh token invalid, re-login required. {ex.Message}");
            return StoreAuthFromCacheStatus.SessionExpired;
        }
    }

    public void OpenLoginPageInBrowser()
    {
        OpenUrl(LoginUrl);
        LogService.WriteInfo("GOG: opened login page in system browser.");
    }

    public async Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new ArgumentException("Authorization code is empty", nameof(authorizationCode));

        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode.Trim(),
            ["redirect_uri"] = RedirectUri
        };

        return await RequestTokenAsync(query);
    }

    public async Task<bool> AuthenticateWithRefreshTokenAsync(string refreshToken)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };

        return await RequestTokenAsync(query);
    }

    private async Task<bool> RequestTokenAsync(Dictionary<string, string> form)
    {
        // GOG's /token endpoint expects the parameters as a GET query string
        // (the same call GOG Galaxy / gogdl make). A recognised User-Agent is required.
        var url = TokenUrl + "?" + string.Join("&",
            form.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        using var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            LogService.WriteError($"GOG token request failed: {response.StatusCode}, {body}");

            // A refresh-token grant rejected with a client error means the cached token
            // is dead — drop it so we don't keep retrying a token that can't work.
            if (form.TryGetValue("grant_type", out var grant) && grant == "refresh_token" &&
                (int)response.StatusCode is >= 400 and < 500)
            {
                LogService.WriteWarning("GOG: cached refresh token rejected — clearing token cache.");
                DeleteTokenCache();
            }

            return false;
        }

        var json = JObject.Parse(body);
        _accessToken = json["access_token"]?.Value<string>();
        _refreshToken = json["refresh_token"]?.Value<string>();
        _userId = json["user_id"]?.Value<string>();

        if (string.IsNullOrEmpty(_accessToken))
        {
            LogService.WriteError("GOG token response has no access_token.");
            return false;
        }

        SaveRefreshTokenToCache(_refreshToken);
        LogService.WriteInfo($"GOG: authenticated as user {_userId}.");
        return true;
    }

    public async Task<List<GogGame>> GetOwnedGamesAsync(IProgress<StoreFetchProgress>? progress = null)
    {
        EnsureAuthenticated();

        var byId = new Dictionary<long, GogGame>();
        var page = 1;
        var totalPages = 1;

        do
        {
            using var response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, string.Format(FilteredProductsUrlTemplate, page)));
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();
            var parsed = JsonConvert.DeserializeObject<GogFilteredProductsResponse>(body);

            totalPages = parsed?.TotalPages > 0 ? parsed.TotalPages : 1;

            // Keep base games only: mediaType=1 already drops movies, and isGame
            // filters out any owned DLC/extra entries (e.g. orphaned DLC).
            if (parsed?.Products != null)
                foreach (var game in parsed.Products)
                    if (game.IsGame && !game.IsMovie)
                        byId[game.Id] = game;

            progress?.Report(new StoreFetchProgress(page, totalPages, "Loading GOG library"));
            page++;
        }
        while (page <= totalPages);

        var games = byId.Values.ToList();
        LogService.WriteInfo($"GOG: fetched {games.Count} owned games.");
        return games;
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException("GOG client is not authenticated. Call authenticate first.");
    }

    // Sends a bearer-authorized request; if the access token has expired (401),
    // refreshes it once and retries. The caller owns/disposes the returned response.
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
                LogService.WriteWarning($"GOG: access-token refresh after 401 failed. {ex.Message}");
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
        var payload = new { refresh_token = refreshToken, user_id = _userId };
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
            LogService.WriteWarning($"GOG: failed to delete token cache: {ex.Message}");
        }
    }

    private string? LoadRefreshTokenFromCache()
    {
        if (!File.Exists(_tokenCachePath)) return null;
        try
        {
            var json = JObject.Parse(File.ReadAllText(_tokenCachePath));
            _userId = json["user_id"]?.Value<string>();
            return json["refresh_token"]?.Value<string>();
        }
        catch (Exception ex)
        {
            LogService.WriteWarning($"GOG: failed to read token cache: {ex.Message}");
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
