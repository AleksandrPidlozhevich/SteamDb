using Newtonsoft.Json;

namespace SteamDb.Models;

public class EpicGame
{
    [JsonProperty("appName")] public string AppName { get; set; } = string.Empty;

    [JsonProperty("catalogItemId")] public string CatalogItemId { get; set; } = string.Empty;

    [JsonProperty("namespace")] public string Namespace { get; set; } = string.Empty;

    [JsonIgnore] public string? Title { get; set; }

    /// <summary>From the catalog item's key images / product slug (set during title enrichment).</summary>
    [JsonIgnore] public string? CoverUrl { get; set; }

    [JsonIgnore] public string? StoreUrl { get; set; }
}