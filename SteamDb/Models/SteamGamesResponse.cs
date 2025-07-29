using Newtonsoft.Json;
using System.Collections.Generic;

namespace SteamDb.Models;

public class SteamGamesResponse
{
    public SteamGamesInnerResponse Response { get; set; }
}

public class SteamGamesInnerResponse
{
    public List<SteamGame> Games { get; set; }
}

public class SteamGame
{
    public string Name { get; set; }

    [JsonProperty("appid")] public int GameID { get; set; }
}