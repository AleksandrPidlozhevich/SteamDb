using System;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>
/// Runs an interactive web login inside an embedded WebView and returns the text payload the
/// authorization code/token is read from — the redirect URL itself (Xbox/GOG) or the redirect
/// page body (Epic) — or null if the user closed the window. The caller's store-specific parser
/// extracts the code from that payload. Abstracts the Avalonia WebView away from the store clients.
/// </summary>
public interface IWebAuthenticator
{
    Task<string?> AuthenticateAsync(Uri requestUri, Uri redirectUri);
}