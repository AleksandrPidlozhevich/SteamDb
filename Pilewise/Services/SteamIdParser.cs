using System;
using System.Text.RegularExpressions;

namespace Pilewise.Services;

/// <summary>
/// What the user typed in the Steam ID field: either a numeric SteamID64 (ready to use) or a
/// custom-URL ("vanity") name that still has to be resolved through <c>ResolveVanityURL</c>.
/// </summary>
public sealed record SteamIdInput(string? SteamId64, string? VanityName)
{
    public bool IsValid => SteamId64 != null || VanityName != null;
}

/// <summary>
/// Accepts every common way of writing a Steam account, so users can paste their profile link
/// instead of hunting for the 17-digit id:
/// <list type="bullet">
/// <item><c>76561197960287930</c> (SteamID64)</item>
/// <item><c>https://steamcommunity.com/profiles/76561197960287930/</c></item>
/// <item><c>https://steamcommunity.com/id/gabelogannewell</c> or just <c>gabelogannewell</c> (vanity)</item>
/// <item><c>[U:1:22202]</c> (SteamID3) and <c>STEAM_0:0:11101</c> (legacy SteamID)</item>
/// </list>
/// </summary>
public static partial class SteamIdParser
{
    // SteamID64 of account number 0 in the public universe (individual accounts).
    private const long IndividualBase = 76561197960265728;

    public static SteamIdInput Parse(string? input)
    {
        var text = input?.Trim().TrimEnd('/') ?? string.Empty;
        if (text.Length == 0) return new SteamIdInput(null, null);

        var match = ProfileUrlRegex().Match(text);
        if (match.Success) return new SteamIdInput(match.Groups[1].Value, null);

        match = VanityUrlRegex().Match(text);
        if (match.Success) return new SteamIdInput(null, Uri.UnescapeDataString(match.Groups[1].Value));

        if (SteamId64Regex().IsMatch(text)) return new SteamIdInput(text, null);

        match = SteamId3Regex().Match(text);
        if (match.Success && long.TryParse(match.Groups[1].Value, out var accountId))
            return new SteamIdInput((IndividualBase + accountId).ToString(), null);

        match = LegacySteamIdRegex().Match(text);
        if (match.Success && long.TryParse(match.Groups[2].Value, out var half))
            return new SteamIdInput((IndividualBase + half * 2 + int.Parse(match.Groups[1].Value)).ToString(), null);

        // Anything else that could be a custom URL name (Steam allows letters, digits, _ and -).
        return VanityNameRegex().IsMatch(text)
            ? new SteamIdInput(null, text)
            : new SteamIdInput(null, null);
    }

    [GeneratedRegex(@"steamcommunity\.com/profiles/(\d{17})(?:[/?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileUrlRegex();

    [GeneratedRegex(@"steamcommunity\.com/id/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VanityUrlRegex();

    [GeneratedRegex(@"^\d{17}$")]
    private static partial Regex SteamId64Regex();

    [GeneratedRegex(@"^\[U:1:(\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId3Regex();

    [GeneratedRegex(@"^STEAM_[0-5]:([01]):(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacySteamIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{2,32}$")]
    private static partial Regex VanityNameRegex();
}
