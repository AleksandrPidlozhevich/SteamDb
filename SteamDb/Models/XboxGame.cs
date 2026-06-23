using System;

namespace SteamDb.Models;

/// <summary>
/// An owned title from the user's Xbox library (the purchased/owned set). The fetch only returns
/// owned games, so <see cref="IsGamePass"/> is no longer set here; the flag is kept for the CSV /
/// Notion "Game Pass" tag (manually-tagged or legacy rows) and stays false for fetched titles.
/// </summary>
public sealed class XboxGame
{
    /// <summary>Decimal Xbox title id (the stable per-title identifier).</summary>
    public string TitleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsGamePass { get; set; }

    public DateTimeOffset? LastPlayed { get; set; }
}