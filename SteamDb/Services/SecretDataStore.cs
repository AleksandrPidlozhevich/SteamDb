using Google.Apis.Json;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SteamDb.Services;

public sealed class SecretDataStore : IDataStore
{
    private readonly ISecretStore _secrets;
    private readonly string _prefix;
    private readonly string _indexKey;

    public SecretDataStore(ISecretStore secrets, string prefix = "google")
    {
        _secrets = secrets;
        _prefix = prefix;
        _indexKey = $"{_prefix}:__keys__";
    }

    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        _secrets.Save(SecretKey(key), json);
        TrackKey(key, true);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        _secrets.Delete(SecretKey(key));
        TrackKey(key, false);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var json = _secrets.Load(SecretKey(key));
        var value = string.IsNullOrEmpty(json)
            ? default!
            : NewtonsoftJsonSerializer.Instance.Deserialize<T>(json);
        return Task.FromResult(value);
    }

    public Task ClearAsync()
    {
        foreach (var key in LoadIndex())
            _secrets.Delete(SecretKey(key));
        _secrets.Delete(_indexKey);
        return Task.CompletedTask;
    }

    private string SecretKey(string key)
    {
        return $"{_prefix}:{key}";
    }

    private void TrackKey(string key, bool add)
    {
        var keys = LoadIndex();
        var changed = add ? keys.Add(key) : keys.Remove(key);
        if (changed)
            _secrets.Save(_indexKey, JsonConvert.SerializeObject(keys.ToArray()));
    }

    private HashSet<string> LoadIndex()
    {
        var json = _secrets.Load(_indexKey);
        if (string.IsNullOrEmpty(json)) return new HashSet<string>();
        return new HashSet<string>(JsonConvert.DeserializeObject<string[]>(json) ?? Array.Empty<string>());
    }
}