using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;


public class FileCache
{
    private readonly string _cacheDir;

    public FileCache(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    private string GetCacheFilePath(string key, string suffix) => Path.Combine(_cacheDir, $"{key}{suffix}.cache");

    public bool TryGetValue<T>(string key, string suffix, out T value)
    {
        string filePath = GetCacheFilePath(key, suffix);
        if (File.Exists(filePath))
        {
            string json = File.ReadAllText(filePath);
            value = JsonSerializer.Deserialize<T>(json);
            IncrementHitCount(key);
            return true;
        }
        value = default(T);
        return false;
    }

    public void SetValue<T>(string key, string suffix, T value)
    {
        string filePath = GetCacheFilePath(key, suffix);
        string json = JsonSerializer.Serialize(value);
        File.WriteAllText(filePath, json);
    }

    private void IncrementHitCount(string key)
    {
        string metaPath = GetCacheFilePath(key, ".meta");
        var meta = File.Exists(metaPath) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(metaPath)) : new Dictionary<string, int> { ["hits"] = 0 };
        meta["hits"] = meta["hits"] + 1;
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
    }

    public void Cleanup(int minHits)
    {
        foreach (var metaFile in Directory.GetFiles(_cacheDir, "*.meta"))
        {
            string key = Path.GetFileNameWithoutExtension(metaFile).Replace(".meta", "");
            var meta = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(metaFile));
            if (meta["hits"] < minHits)
            {
                foreach (var suffix in new[] { "_tokens", "_intermediate", "_final", ".meta" })
                {
                    string filePath = GetCacheFilePath(key, suffix);
                    if (File.Exists(filePath)) File.Delete(filePath);
                }
            }
        }
    }
}
