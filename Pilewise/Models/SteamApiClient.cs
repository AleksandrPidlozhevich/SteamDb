using Newtonsoft.Json;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Models;

public class SteamApiClient : ISteamClient
{
    private const int MaxRetries = 5;
    private static readonly HttpClient _httpClient = new();

    private readonly ILogService _log;

    public SteamApiClient(ILogService log)
    {
        _log = log;
    }

    public async Task<SteamGamesResponse> GetOwnedGames(string? steamId, string? apiKey, CancellationToken ct = default)
    {
        apiKey = apiKey?.Trim();
        steamId = steamId?.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Steam API key cannot be empty", nameof(apiKey));

        if (string.IsNullOrWhiteSpace(steamId))
            throw new ArgumentException("Steam ID cannot be empty", nameof(steamId));

        steamId = await ResolveSteamIdAsync(steamId, apiKey, ct);

        var retryCount = 0;
        while (true)
            try
            {
                // include_played_free_games=1 so free games (e.g. demos/prologues) are returned too;
                // without it Steam omits them and they never reach the export.
                var url =
                    $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={apiKey}&steamid={steamId}&format=json&include_appinfo=1&include_played_free_games=1";
                var response = await _httpClient.GetAsync(url, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (retryCount >= MaxRetries)
                    {
                        _log.WriteError("Exceeded max retries due to rate limiting (429).");
                        throw new Exception(
                            "The attempt limit has been exceeded. Steam API is temporarily unavailable due to request restrictions.");
                    }

                    var delaySeconds = Math.Pow(2, retryCount);
                    _log.WriteWarning($"Received 429 TooManyRequests. Retrying in {delaySeconds:F1} seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                    retryCount++;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _log.WriteError("Steam API key is invalid (401 Unauthorized).");
                    throw new UnauthorizedAccessException("Invalid Steam API key. Check the correctness of the key.");
                }

                response.EnsureSuccessStatusCode();

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<SteamGamesResponse>(jsonResponse);

                if (result?.Response?.Games == null)
                {
                    _log.WriteError("Steam API returned empty or invalid game list.");
                    throw new Exception(
                        "Failed to receive a list of games. Check the Steam ID correctness and adjust the privacy of the profile.");
                }

                _log.WriteInfo($"Steam API returned {result.Response.Games.Count} games.");
                return result;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429") && retryCount < MaxRetries)
            {
                var delaySeconds = Math.Pow(2, retryCount);
                _log.WriteWarning(
                    $"Caught HttpRequestException with 429: retry {retryCount + 1}, waiting {delaySeconds:F1}s");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                retryCount++;
            }
            catch (HttpRequestException ex)
            {
                _log.WriteException(ex, "Error when contacting Steam API");
                throw new Exception(
                    $"Error when contacting Steam API: {ex.Message}\nCheck:\n1. The correctness of the API key\n2. Correct Steam ID\n3. Setting the Privacy of Steam Profile",
                    ex);
            }
            catch (Exception ex)
            {
                _log.WriteException(ex, "Unexpected error in GetOwnedGames");
                throw;
            }
    }

    /// <summary>
    /// Turns whatever the user entered (SteamID64, profile link, vanity name, SteamID3…) into a
    /// SteamID64, resolving custom-URL names through <c>ISteamUser/ResolveVanityURL</c>.
    /// </summary>
    private async Task<string> ResolveSteamIdAsync(string input, string apiKey, CancellationToken ct)
    {
        var parsed = SteamIdParser.Parse(input);
        if (parsed.SteamId64 != null) return parsed.SteamId64;
        if (parsed.VanityName == null)
            throw new ArgumentException(
                "Steam ID not recognised. Enter your 17-digit Steam ID or paste your Steam profile link.");

        var url = "https://api.steampowered.com/ISteamUser/ResolveVanityURL/v0001/" +
                  $"?key={apiKey}&vanityurl={Uri.EscapeDataString(parsed.VanityName)}";
        var response = await _httpClient.GetAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Invalid Steam API key. Check the correctness of the key.");
        response.EnsureSuccessStatusCode();

        var json = JsonConvert.DeserializeObject<VanityResponse>(await response.Content.ReadAsStringAsync(ct));
        // success: 1 = resolved, 42 = no profile with that custom URL.
        if (json?.Response?.Success != 1 || string.IsNullOrEmpty(json.Response.SteamId))
            throw new ArgumentException(
                $"No Steam profile found for \"{parsed.VanityName}\". " +
                "Check the name, or paste your profile link / 17-digit Steam ID instead.");

        _log.WriteInfo($"Steam: resolved custom URL \"{parsed.VanityName}\" to a SteamID64.");
        return json.Response.SteamId;
    }

    private sealed class VanityResponse
    {
        [JsonProperty("response")] public VanityResult? Response { get; set; }
    }

    private sealed class VanityResult
    {
        [JsonProperty("success")] public int Success { get; set; }

        [JsonProperty("steamid")] public string? SteamId { get; set; }
    }
}
