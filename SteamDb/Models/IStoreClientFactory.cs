using SteamDb.Services;

namespace SteamDb.Models;

/// <summary>
/// Creates fresh store clients on demand. A new client is built per connect/fetch operation so it
/// reloads the cached session from the (shared) secret store; the factory just supplies the store
/// dependency so callers don't construct clients — and reach for the secret store — themselves.
/// </summary>
public interface IStoreClientFactory
{
    EpicApiClient CreateEpic();

    GogApiClient CreateGog();

    XboxApiClient CreateXbox();
}

public sealed class StoreClientFactory : IStoreClientFactory
{
    private readonly ISecretStore _secrets;
    private readonly ILogService _log;

    public StoreClientFactory(ISecretStore secrets, ILogService log)
    {
        _secrets = secrets;
        _log = log;
    }

    public EpicApiClient CreateEpic() => new(_secrets, _log);

    public GogApiClient CreateGog() => new(_secrets, _log);

    public XboxApiClient CreateXbox() => new(_secrets, _log);
}
