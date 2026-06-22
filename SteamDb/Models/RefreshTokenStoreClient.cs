using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Models;

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

/// <summary>
/// Shared base for the OAuth store clients that use the authorization-code → refresh-token bearer
/// flow (Epic, GOG): the user logs in via the browser, the resulting code is exchanged for tokens,
/// the refresh token is cached (encrypted) and renewed on demand, with a one-shot refresh + retry
/// on HTTP 401. Subclasses supply the endpoints/credentials, the token-request transport, and the
/// library fetch; everything else lives here.
/// </summary>
public abstract class RefreshTokenStoreClient : IStoreClient
{
    protected readonly ISecretStore Secrets;
    protected readonly ILogService Log;

    // Serialises access-token refreshes so concurrent requests that hit a 401 don't fire
    // several refreshes at once (the refresh token may be rotated on each use).
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    protected string? AccessToken;
    protected string? RefreshToken;

    // The account/user id returned alongside the tokens (purely informational, kept in the cache).
    protected string? AccountId;

    protected RefreshTokenStoreClient(ISecretStore secretStore, ILogService log)
    {
        Secrets = secretStore;
        Log = log;
    }

    // ---- Subclass surface ------------------------------------------------------------

    /// <summary>Store name used in log messages (e.g. "Epic", "GOG").</summary>
    protected abstract string StoreName { get; }

    /// <summary>Key under which the refresh-token payload is kept in the secret store.</summary>
    protected abstract string SecretKey { get; }

    /// <summary>The (shared, static) HttpClient configured with the store's required headers.</summary>
    protected abstract HttpClient Http { get; }

    /// <summary>Name of the id field returned by the token endpoint (e.g. "account_id", "user_id").</summary>
    protected abstract string IdFieldName { get; }

    /// <summary>The login URL the browser / embedded WebView starts at.</summary>
    protected abstract string BrowserLoginUrl { get; }

    public Uri LoginRequestUri => new(BrowserLoginUrl);

    public abstract Uri LoginRedirectUri { get; }

    /// <summary>Builds the token-endpoint form for an authorization-code grant.</summary>
    protected abstract Dictionary<string, string> BuildAuthCodeForm(string authorizationCode);

    /// <summary>Builds the token-endpoint form for a refresh-token grant.</summary>
    protected abstract Dictionary<string, string> BuildRefreshForm(string refreshToken);

    /// <summary>Sends the token request (POST vs GET / auth header differ per store).</summary>
    protected abstract Task<HttpResponseMessage> SendTokenRequestAsync(Dictionary<string, string> form);

    /// <summary>Legacy plaintext token file to migrate from, or null if the store never had one.</summary>
    protected virtual (string Folder, string FileName)? LegacyTokenPath => null;

    // ---- IStoreClient ----------------------------------------------------------------

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken);

    public void SignOut()
    {
        AccessToken = null;
        RefreshToken = null;
        AccountId = null;
        Secrets.Delete(SecretKey);
        Log.WriteInfo($"{StoreName}: signed out (cleared cached session).");
    }

    public void OpenLoginPageInBrowser()
    {
        SystemBrowser.Open(BrowserLoginUrl);
        Log.WriteInfo($"{StoreName}: opened login page in system browser.");
    }

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
            Log.WriteWarning($"{StoreName}: refresh token invalid, re-login required. {ex.Message}");
            return StoreAuthFromCacheStatus.SessionExpired;
        }
    }

    public Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode)
    {
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new ArgumentException("Authorization code is empty", nameof(authorizationCode));

        return RequestTokenAsync(BuildAuthCodeForm(authorizationCode.Trim()));
    }

    public Task<bool> AuthenticateWithRefreshTokenAsync(string refreshToken)
    {
        return RequestTokenAsync(BuildRefreshForm(refreshToken));
    }

    // ---- Token plumbing --------------------------------------------------------------

    private async Task<bool> RequestTokenAsync(Dictionary<string, string> form)
    {
        using var response = await SendTokenRequestAsync(form);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Log.WriteError($"{StoreName} token request failed: {response.StatusCode}, {body}");

            // A refresh-token grant rejected with a client error means the cached token is dead —
            // drop it so we don't keep retrying a token that can't work.
            if (form.TryGetValue("grant_type", out var grant) && grant == "refresh_token" &&
                (int)response.StatusCode is >= 400 and < 500)
            {
                Log.WriteWarning($"{StoreName}: cached refresh token rejected — clearing token cache.");
                Secrets.Delete(SecretKey);
            }

            return false;
        }

        var json = JObject.Parse(body);
        AccessToken = json["access_token"]?.Value<string>();
        RefreshToken = json["refresh_token"]?.Value<string>();
        AccountId = json[IdFieldName]?.Value<string>();

        if (string.IsNullOrEmpty(AccessToken))
        {
            Log.WriteError($"{StoreName} token response has no access_token.");
            return false;
        }

        SaveRefreshTokenToCache(RefreshToken);
        Log.WriteInfo($"{StoreName}: authenticated as {AccountId}.");
        return true;
    }

    /// <summary>
    /// Sends a bearer-authorized request, refreshing the access token once and retrying on a 401.
    /// The caller owns/disposes the returned response.
    /// </summary>
    protected async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct = default)
    {
        var staleToken = AccessToken;

        var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        var response = await Http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized &&
            await TryRefreshAccessTokenAsync(staleToken))
        {
            response.Dispose();
            request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            response = await Http.SendAsync(request, ct);
        }

        return response;
    }

    private async Task<bool> TryRefreshAccessTokenAsync(string? staleAccessToken)
    {
        await _refreshLock.WaitAsync();
        try
        {
            // A concurrent request may already have refreshed while we waited on the lock.
            if (!string.IsNullOrEmpty(AccessToken) && AccessToken != staleAccessToken)
                return true;

            var rt = RefreshToken ?? LoadRefreshTokenFromCache();
            if (string.IsNullOrEmpty(rt)) return false;

            try
            {
                return await AuthenticateWithRefreshTokenAsync(rt);
            }
            catch (Exception ex)
            {
                Log.WriteWarning($"{StoreName}: access-token refresh after 401 failed. {ex.Message}");
                return false;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    protected void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException($"{StoreName} client is not authenticated. Call authenticate first.");
    }

    // ---- Token cache -----------------------------------------------------------------

    private void SaveRefreshTokenToCache(string? refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;
        var payload = new Dictionary<string, object?>
        {
            ["refresh_token"] = refreshToken,
            [IdFieldName] = AccountId
        };
        Secrets.Save(SecretKey, JsonConvert.SerializeObject(payload));
    }

    private string? LoadRefreshTokenFromCache()
    {
        var content = Secrets.Load(SecretKey);
        if (string.IsNullOrEmpty(content)) return null;
        try
        {
            var json = JObject.Parse(content);
            AccountId = json[IdFieldName]?.Value<string>();
            return json["refresh_token"]?.Value<string>();
        }
        catch (Exception ex)
        {
            Log.WriteWarning($"{StoreName}: failed to read token cache: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// One-time migration of a legacy plaintext token file (next to the exe) into the encrypted
    /// secret store. Subclasses with such a file call this from their constructor.
    /// </summary>
    protected void MigrateLegacyTokenFile()
    {
        if (LegacyTokenPath is not { } path) return;
        var (folder, fileName) = path;

        try
        {
            var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            var legacyPath = Path.Combine(exeDir, folder, fileName);
            if (!File.Exists(legacyPath)) return;

            if (string.IsNullOrEmpty(Secrets.Load(SecretKey)))
                Secrets.Save(SecretKey, File.ReadAllText(legacyPath));

            File.Delete(legacyPath);
            Log.WriteInfo($"{StoreName}: migrated legacy plaintext token into encrypted store.");
        }
        catch (Exception ex)
        {
            Log.WriteWarning($"{StoreName}: legacy token migration failed: {ex.Message}");
        }
    }
}
