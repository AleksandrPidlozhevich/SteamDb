using System;

namespace SteamDb.Models;

/// <summary>
/// A title from the user's Xbox account. <see cref="IsGamePass"/> marks a game that was played
/// but is not in the owned/purchased set — i.e. accessed through Game Pass (best-effort: it falls
/// back to false when the owned set could not be determined).
/// </summary>
public sealed class XboxGame
{
    /// <summary>Decimal Xbox title id (the stable per-title identifier).</summary>
    public string TitleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsGamePass { get; set; }

    public DateTimeOffset? LastPlayed { get; set; }
}