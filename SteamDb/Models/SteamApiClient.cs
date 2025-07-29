using Newtonsoft.Json;
using SteamDb.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SteamDb.Models;

public class SteamApiClient
{
    private const int MaxRetries = 5;
    private static readonly HttpClient _httpClient = new();

    public async Task<SteamGamesResponse> GetOwnedGames(string steamId, string apiKey)
    {
        apiKey = apiKey?.Trim();
        steamId = steamId?.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Steam API key cannot be empty", nameof(apiKey));

        if (string.IsNullOrWhiteSpace(steamId))
            throw new ArgumentException("Steam ID cannot be empty", nameof(steamId));

        var retryCount = 0;
        while (true)
            try
            {
                var url =
                    $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/?key={apiKey}&steamid={steamId}&format=json&include_appinfo=1";
                var response = await _httpClient.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (retryCount >= MaxRetries)
                    {
                        LogService.WriteError("Exceeded max retries due to rate limiting (429).");
                        throw new Exception(
                            "The attempt limit has been exceeded. Steam API is temporarily unavailable due to request restrictions.");
                    }

                    var delaySeconds = Math.Pow(2, retryCount);
                    LogService.WriteWarning($"Received 429 TooManyRequests. Retrying in {delaySeconds:F1} seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                    retryCount++;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    LogService.WriteError("Steam API key is invalid (401 Unauthorized).");
                    throw new UnauthorizedAccessException("Invalid Steam API key. Check the correctness of the key.");
                }

                response.EnsureSuccessStatusCode();

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<SteamGamesResponse>(jsonResponse);

                if (result?.Response?.Games == null)
                {
                    LogService.WriteError("Steam API returned empty or invalid game list.");
                    throw new Exception(
                        "Failed to receive a list of games. Check the Steam ID correctness and adjust the privacy of the profile.");
                }

                LogService.WriteInfo($"Steam API returned {result.Response.Games.Count} games.");
                return result;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429") && retryCount < MaxRetries)
            {
                var delaySeconds = Math.Pow(2, retryCount);
                LogService.WriteWarning(
                    $"Caught HttpRequestException with 429: retry {retryCount + 1}, waiting {delaySeconds:F1}s");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                retryCount++;
            }
            catch (HttpRequestException ex)
            {
                LogService.WriteException(ex, "Error when contacting Steam API");
                throw new Exception(
                    $"Error when contacting Steam API: {ex.Message}\nCheck:\n1. The correctness of the API key\n2. Correct Steam ID\n3. Setting the Privacy of Steam Profile",
                    ex);
            }
            catch (Exception ex)
            {
                LogService.WriteException(ex, "Unexpected error in GetOwnedGames");
                throw;
            }
    }
}