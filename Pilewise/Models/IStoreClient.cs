using System;
using System.Threading.Tasks;

namespace SteamDb.Models;

/// <summary>
/// The connect/auth surface shared by store clients (Epic, GOG, Xbox). They authorize via an
/// interactive web login; the resulting code/token is captured either from an embedded WebView
/// (<see cref="LoginRequestUri"/> / <see cref="LoginRedirectUri"/>) or the legacy paste flow.
/// </summary>
public interface IStoreClient
{
    Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync();

    /// <summary>Clears the cached session (forgets the refresh token) so the store is disconnected.</summary>
    void SignOut();

    void OpenLoginPageInBrowser();

    Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode);

    /// <summary>The login page URL where the embedded WebView starts.</summary>
    Uri LoginRequestUri { get; }

    /// <summary>The redirect URL whose navigation ends the login and carries the code/token.</summary>
    Uri LoginRedirectUri { get; }
}