namespace Pilewise.Services;

/// <summary>
/// Plain local storage for app state that isn't secret (window placement, sync log, hidden games…).
/// Secrets (tokens, API keys) go to <see cref="ISecretStore"/> instead.
/// </summary>
public interface IStateStore
{
    string? Load(string key);

    void Save(string key, string value);

    void Delete(string key);
}
