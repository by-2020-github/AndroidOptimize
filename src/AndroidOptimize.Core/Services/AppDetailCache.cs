using System.Text.Json;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

/// <summary>
/// 一个应用的「深度信息」缓存：安装来源、安装时间、权限列表、有没有桌面图标。
///
/// 这些字段只跟**包名 + 版本号**有关——申请了哪些权限写在 APK 里，应用不更新就不会变，
/// 所以缓存是安全的，也是把重复扫描从 10 秒压到 1~2 秒的关键。
/// 需要最新状态（例如刚在手机上改过什么）时，界面上勾「强制重新读取权限」即可。
///
/// 注意：「从未启动过」这类**运行状态**也会跟着缓存，所以它可能是上次扫描的结果。
/// </summary>
public sealed class AppDetailCache
{
    /// <summary>
    /// 读取逻辑版本号，参与 key。改了 DeviceScanner.ParsePackageDetail 或解析权限的规则就加一，
    /// 让旧的缓存整体作废，避免继续用老逻辑读出来的数据。
    /// </summary>
    private const string ReaderVersion = "r1";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly Dictionary<string, CachedPackageDetail> _entries;
    private readonly string _path;
    private bool _dirty;

    private AppDetailCache(Dictionary<string, CachedPackageDetail> entries, string path)
    {
        _entries = entries;
        _path = path;
    }

    public int Count => _entries.Count;

    public static AppDetailCache Load(string? path = null)
    {
        path ??= AppPaths.DetailCacheFile;
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, CachedPackageDetail>>(File.ReadAllText(path), Options);
                if (data is not null) return new AppDetailCache(data, path);
            }
        }
        catch
        {
            // 缓存坏了就重建，只是这一次扫描慢一点。
        }

        return new AppDetailCache(new Dictionary<string, CachedPackageDetail>(StringComparer.OrdinalIgnoreCase), path);
    }

    public CachedPackageDetail? Get(string packageName, string? version)
    {
        var key = Key(packageName, version);
        return _entries.TryGetValue(key, out var detail) ? detail : null;
    }

    public void Set(string packageName, string? version, CachedPackageDetail detail)
    {
        var key = Key(packageName, version);
        _entries[key] = detail;
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            // 顺手清掉旧读取逻辑留下的记录，免得文件越滚越大。
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
            // 写缓存失败不影响本次扫描。
        }
    }

    private static string Key(string packageName, string? version) =>
        $"{packageName.ToLowerInvariant()}@{version ?? "?"}#{ReaderVersion}";
}

/// <summary>缓存下来的深度信息。字段和 DeviceScanner 解析 dumpsys 得到的那些一一对应。</summary>
public sealed class CachedPackageDetail
{
    public string? Installer { get; init; }
    public string? VersionName { get; init; }
    public string? CodePath { get; init; }
    public DateTimeOffset? FirstInstallTime { get; init; }
    public DateTimeOffset? LastUpdateTime { get; init; }
    public bool? NeverLaunched { get; init; }
    public bool HasLauncher { get; init; }
    public IReadOnlyList<string>? Permissions { get; init; }

    /// <summary>这条缓存是什么时候读的，方便排查「数据是不是旧的」。</summary>
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>从设备上读到的东西原样记下来（DeviceScanner 内部用）。</summary>
    public static CachedPackageDetail From(PackageEntry entry) => new()
    {
        Installer = entry.Installer,
        VersionName = entry.VersionName,
        CodePath = entry.CodePath,
        FirstInstallTime = entry.FirstInstallTime,
        LastUpdateTime = entry.LastUpdateTime,
        NeverLaunched = entry.NeverLaunched,
        HasLauncher = entry.HasLauncher ?? false,
        Permissions = entry.Permissions,
    };

    public PackageEntry ApplyTo(PackageEntry entry) => entry with
    {
        Installer = Installer,
        VersionName = VersionName ?? entry.VersionName,
        CodePath = CodePath,
        FirstInstallTime = FirstInstallTime,
        LastUpdateTime = LastUpdateTime,
        NeverLaunched = NeverLaunched,
        HasLauncher = HasLauncher,
        Permissions = Permissions,
    };
}
