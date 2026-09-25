using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

/// <summary>
/// 离线名册：包名 → 「这是什么 + 社区认为能不能动」。
///
/// 数据来自 Universal-Debloat-Alliance 的 uad_lists.json，由 tools/build-app-catalog.py 生成。
/// 与 packages.json 的分工是刻意的：
///   规则库 = 决策（删/留），人工把关，写错会误删；
///   名册   = 描述，批量生成，写错只是显示不对。
/// </summary>
public sealed class AppCatalog
{
    private readonly Dictionary<string, CatalogEntry> _entries;

    private AppCatalog(Dictionary<string, CatalogEntry> entries, ListSource source, string? generatedAt, string? origin)
    {
        _entries = entries;
        Source = source;
        GeneratedAt = generatedAt;
        Origin = origin;
    }

    public int Count => _entries.Count;
    public ListSource Source { get; }
    public string? GeneratedAt { get; }
    public string? Origin { get; }

    public static AppCatalog Load(string? appBaseDirectory = null)
    {
        var warnings = new List<string>();
        var file = DataFileLoader.Load<AppCatalogFile>(
            "app-catalog.json", appBaseDirectory ?? AppContext.BaseDirectory, out var source, warnings);

        if (file is null)
        {
            return new AppCatalog(new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase),
                ListSource.Builtin, null, null);
        }

        var entries = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, entry) in file.Entries)
        {
            if (!string.IsNullOrWhiteSpace(package)) entries[package] = entry;
        }

        return new AppCatalog(entries, source, file.GeneratedAt, file.Source);
    }

    public CatalogEntry? Find(string packageName) =>
        _entries.TryGetValue(packageName, out var entry) ? entry : null;

    /// <summary>构造一个内存名册，供自检与测试使用。</summary>
    public static AppCatalog FromEntries(IDictionary<string, CatalogEntry> entries)
    {
        return new AppCatalog(
            new Dictionary<string, CatalogEntry>(entries, StringComparer.OrdinalIgnoreCase),
            ListSource.Builtin, null, "自检构造");
    }

    /// <summary>UAD 是否认为这个应用可以安全移除。</summary>
    public bool IsRecommended(string packageName) =>
        string.Equals(Find(packageName)?.Removal, "recommended", StringComparison.OrdinalIgnoreCase);
}
