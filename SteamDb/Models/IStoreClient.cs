using System.Threading.Tasks;

namespace SteamDb.Models;

/// <summary>
/// The connect/auth surface shared by store clients (Epic, GOG) that use the
/// "log in via browser, paste the authorization code" OAuth flow.
/// </summary>
public interface IStoreClient
{
    Task<StoreAuthFromCacheStatus> TryAuthenticateFromCacheAsync();

    void OpenLoginPageInBrowser();

    Task<bool> AuthenticateWithAuthorizationCodeAsync(string authorizationCode);
}