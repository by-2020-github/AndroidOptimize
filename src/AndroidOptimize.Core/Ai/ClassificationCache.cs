using System.Text.Json;
using System.Text.Json.Serialization;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Ai;

/// <summary>
/// AI 识别结果缓存。同一个包只问一次，之后永久命中本地缓存，重复运行零成本。
/// </summary>
public sealed class ClassificationCache
{
    public const string PromptVersion = "v1";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Dictionary<string, CacheEntry> _entries;
    private readonly string _path;

    private ClassificationCache(Dictionary<string, CacheEntry> entries, string path)
    {
        _entries = entries;
        _path = path;
    }

    public int Count => _entries.Count;

    public static ClassificationCache Load(string? path = null)
    {
        path ??= Services.AppPaths.AiCacheFile;
        try
        {
            if (File.Exists(path))
            {
                var store = JsonSerializer.Deserialize<CacheStore>(File.ReadAllText(path), Options);
                if (store?.Entries is not null)
                {
                    return new ClassificationCache(store.Entries, path);
                }
            }
        }
        catch
        {
            // 缓存损坏直接重建。
        }

        return new ClassificationCache(new Dictionary<string, CacheEntry>(), path);
    }

    public PackageClassification? Get(string packageName, string model)
    {
        if (_entries.TryGetValue(Key(packageName, model), out var entry))
        {
            return new PackageClassification
            {
                PackageName = packageName,
                Category = entry.Category,
                Action = entry.Action,
                Confidence = entry.Confidence,
                Reason = entry.Reason,
                Source = "cache",
            };
        }
        return null;
    }

    public void Set(PackageClassification classification, string model)
    {
        _entries[Key(classification.PackageName, model)] = new CacheEntry
        {
            Category = classification.Category,
            Action = classification.Action,
            Confidence = classification.Confidence,
            Reason = classification.Reason,
            At = DateTimeOffset.Now,
        };
    }

    public void Save()
    {
        try
        {
            Services.AppPaths.EnsureCreated();
            var store = new CacheStore { Entries = _entries };
            File.WriteAllText(_path, JsonSerializer.Serialize(store, Options));
        }
        catch
        {
            // 缓存写失败不影响本次结果。
        }
    }

    private static string Key(string packageName, string model) =>
        $"{packageName.ToLowerInvariant()}|{model}|{PromptVersion}";

    private sealed class CacheStore
    {
        [JsonPropertyName("version")] public int Version { get; init; } = 1;
        [JsonPropertyName("entries")] public Dictionary<string, CacheEntry> Entries { get; init; } = new();
    }

    private sealed class CacheEntry
    {
        [JsonPropertyName("category")] public string Category { get; init; } = "未知";
        [JsonPropertyName("action")] public PackageAction Action { get; init; } = PackageAction.Keep;
        [JsonPropertyName("confidence")] public double Confidence { get; init; }
        [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
        [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
    }
}
