using Newtonsoft.Json;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace SteamDb.Models;

/// <summary>
/// Minimal client for GOG's (unofficial) Galaxy OAuth API. The shared refresh-token bearer
/// plumbing lives in <see cref="RefreshTokenStoreClient"/>; this adds the GOG endpoints, the
/// GET-query-string token request, and the paginated owned-games fetch.
/// </summary>
public class GogApiClient : RefreshTokenStoreClient
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

    public GogApiClient(ISecretStore? secretStore = null) : base(secretStore)
    {
        MigrateLegacyTokenFile();
    }

    protected override string StoreName => "GOG";
    protected override string SecretKey => "gog";
    protected override HttpClient Http => _httpClient;
    protected override string IdFieldName => "user_id";

    protected override string BrowserLoginUrl => LoginUrl;

    public override Uri LoginRedirectUri => new("https://embed.gog.com/on_login_success");

    protected override (string Folder, string FileName)? LegacyTokenPath => ("GogTokenStorage", "gog_token.json");

    protected override Dictionary<string, string> BuildAuthCodeForm(string authorizationCode)
    {
        return new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = RedirectUri
        };
    }

    protected override Dictionary<string, string> BuildRefreshForm(string refreshToken)
    {
        return new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };
    }

    protected override Task<HttpResponseMessage> SendTokenRequestAsync(Dictionary<string, string> form)
    {
        // GOG's /token endpoint expects the parameters as a GET query string
        // (the same call GOG Galaxy / gogdl make). A recognised User-Agent is required.
        var url = TokenUrl + "?" + string.Join("&",
            form.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        return Http.GetAsync(url);
    }

    public async Task<List<GogGame>> GetOwnedGamesAsync(IProgress<StoreFetchProgress>? progress = null)
    {
        EnsureAuthenticated();

        var byId = new Dictionary<long, GogGame>();
        var page = 1;
        var totalPages = 1;

        do
        {
            using var response = await SendAuthorizedAsync(() =>
                new HttpRequestMessage(HttpMethod.Get, string.Format(FilteredProductsUrlTemplate, page)));
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
        } while (page <= totalPages);

        var games = byId.Values.ToList();
        LogService.WriteInfo($"GOG: fetched {games.Count} owned games.");
        return games;
    }
}
