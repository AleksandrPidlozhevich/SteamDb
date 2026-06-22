using System.Text.RegularExpressions;

namespace SteamDb.Services;

/// <summary>
/// Detects the Microsoft (live.com) implicit-flow redirect from arbitrary clipboard / pasted text.
/// The MBI_SSL flow returns its tokens in the redirect URL fragment
/// (<c>…/oauth20_desktop.srf#access_token=…&amp;refresh_token=…</c>), so — unlike Epic/GOG, which
/// hand back a bare code — this returns the whole URL when it carries an <c>access_token</c>; the
/// Xbox client parses the tokens out of it.
/// </summary>
public static class XboxAuthCodeParser
{
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();

        return Regex.IsMatch(text, "[#?&]access_token=", RegexOptions.IgnoreCase) ? text : null;
    }
}