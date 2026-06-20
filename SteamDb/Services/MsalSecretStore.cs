using Microsoft.Identity.Client.Extensions.Msal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SteamDb.Services;

public sealed class MsalSecretStore : ISecretStore
{
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, Storage> _byKey = new();

    public MsalSecretStore(string? folder = null)
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        _folder = folder ?? Path.Combine(exeDir, "SecretStorage");
        if (!Directory.Exists(_folder)) Directory.CreateDirectory(_folder);
    }

    public string? Load(string key)
    {
        try
        {
            var bytes = GetStorage(key).ReadData();
            return bytes is { Length: > 0 } ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch (Exception ex)
        {
            LogService.WriteWarning($"SecretStore: failed to read '{key}': {ex.Message}");
            return null;
        }
    }

    public void Save(string key, string value)
    {
        try
        {
            GetStorage(key).WriteData(Encoding.UTF8.GetBytes(value));
        }
        catch (Exception ex)
        {
            LogService.WriteWarning($"SecretStore: failed to write '{key}': {ex.Message}");
        }
    }

    public void Delete(string key)
    {
        try
        {
            GetStorage(key).Clear();
        }
        catch (Exception ex)
        {
            LogService.WriteWarning($"SecretStore: failed to delete '{key}': {ex.Message}");
        }
    }

    private Storage GetStorage(string key) => _byKey.GetOrAdd(key, BuildStorage);

    private Storage BuildStorage(string key)
    {
        var fileName = SafeFileName(key) + ".bin";
        
        var props = new StorageCreationPropertiesBuilder(fileName, _folder)
            .WithMacKeyChain($"SteamDb.{key}", key)
            .WithLinuxKeyring(
                $"com.steamdb.secrets.{key}",
                "default",
                $"SteamDb secret: {key}",
                new KeyValuePair<string, string>("Version", "1"),
                new KeyValuePair<string, string>("Key", key))
            .Build();

        return Storage.Create(props);
    }
    
    private static string SafeFileName(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        return $"{sb}.{hash}";
    }
}
