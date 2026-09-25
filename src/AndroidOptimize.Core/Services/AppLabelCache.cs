using System.Text.Json;

namespace AndroidOptimize.Core.Services;

/// <summary>
/// 应用名缓存。读一次 APK 要几百毫秒，缓存之后重复扫描几乎不花时间。
/// key 带上版本号：应用更新后名字可能变，需要重新读。
/// </summary>
public sealed class AppLabelCache
{
    /// <summary>
    /// 读取逻辑的版本号，参与缓存 key。改了「怎么读应用名」就把这个数字加一，
    /// 这样之前记下的「读不出来」会作废并重试一次——否则改进解析器之后，
    /// 老的失败记录会一直被跳过，用户永远看不到修好的结果。
    /// </summary>
    private const string ReaderVersion = "r2";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly Dictionary<string, string> _entries;
    private readonly string _path;
    private bool _dirty;

    private AppLabelCache(Dictionary<string, string> entries, string path)
    {
        _entries = entries;
        _path = path;
    }

    public int Count => _entries.Count;

    public static AppLabelCache Load(string? path = null)
    {
        path ??= AppPaths.LabelCacheFile;
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Options);
                if (data is not null) return new AppLabelCache(data, path);
            }
        }
        catch
        {
            // 缓存损坏就重建，不影响功能。
        }

        return new AppLabelCache(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), path);
    }

    public string? Get(string packageName, string? version)
    {
        var key = Key(packageName, version);
        return _entries.TryGetValue(key, out var label) && !string.IsNullOrWhiteSpace(label) ? label : null;
    }

    /// <summary>
    /// 这个版本之前读过、但读不出来（写成了空字符串）。用来**不重复重试**：
    /// 一台手机上的 RRO overlay、系统内部组件本来就没有应用名，
    /// 每次扫描都去读一遍只会白花几十秒。
    /// </summary>
    public bool IsKnownUnreadable(string packageName, string? version)
    {
        var key = Key(packageName, version);
        return _entries.TryGetValue(key, out var label) && string.IsNullOrEmpty(label);
    }

    public void SetUnreadable(string packageName, string? version)
    {
        var key = Key(packageName, version);
        if (_entries.TryGetValue(key, out var existing) && string.IsNullOrEmpty(existing)) return;

        _entries[key] = string.Empty;
        _dirty = true;
    }

    public void Set(string packageName, string? version, string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        var key = Key(packageName, version);
        if (_entries.TryGetValue(key, out var existing) && existing == label) return;

        _entries[key] = label;
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            // 顺手清掉旧读取逻辑留下的记录：改了 ReaderVersion 之后它们再也不会被命中，
            // 留着只会让缓存文件越长越大。
            var suffix = "#" + ReaderVersion;
            foreach (var stale in _entries.Keys.Where(k => !k.EndsWith(suffix, StringComparison.Ordinal)).ToList())
            {
                _entries.Remove(stale);
            }

            AppPaths.EnsureCreated();
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Options));
            _dirty = false;
        }
        catch
        {
            // 缓存写失败不影响本次扫描。
        }
    }

    private static string Key(string packageName, string? version) =>
        $"{packageName.ToLowerInvariant()}@{version ?? "?"}#{ReaderVersion}";
}
