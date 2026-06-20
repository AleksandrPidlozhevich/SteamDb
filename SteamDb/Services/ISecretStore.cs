namespace SteamDb.Services;

public interface ISecretStore
{
    string? Load(string key);

    void Save(string key, string value);

    void Delete(string key);
}