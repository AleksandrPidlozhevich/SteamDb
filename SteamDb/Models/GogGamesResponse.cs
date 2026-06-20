using Newtonsoft.Json;
using System.Collections.Generic;

namespace SteamDb.Models;

public class GogGame
{
    [JsonProperty("id")] public long Id { get; set; }

    [JsonProperty("title")] public string Title { get; set; } = string.Empty;

    [JsonProperty("isGame")] public bool IsGame { get; set; }

    [JsonProperty("isMovie")] public bool IsMovie { get; set; }
}

/// <summary>One page of the GOG <c>getFilteredProducts</c> response.</summary>
public class GogFilteredProductsResponse
{
    [JsonProperty("page")] public int Page { get; set; }

    [JsonProperty("totalPages")] public int TotalPages { get; set; }

    [JsonProperty("products")] public List<GogGame> Products { get; set; } = new();
}
