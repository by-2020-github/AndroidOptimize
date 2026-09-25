using System.Text.Json.Serialization;

namespace AndroidOptimize.Core.Models;

/// <summary>
/// 离线名册里的一条：这个应用是干什么的、社区认为能不能动。
/// 注意它**只描述、不决策**——写错了顶多显示不对，不会导致误删。
/// </summary>
public sealed record CatalogEntry
{
    /// <summary>UAD 社区的结论：recommended / advanced / expert / unsafe。</summary>
    [JsonPropertyName("removal")] public string Removal { get; init; } = string.Empty;

    /// <summary>来源分类：oem / aosp / misc / carrier / google。</summary>
    [JsonPropertyName("list")] public string List { get; init; } = string.Empty;

    /// <summary>中文说明（机器翻译 + 人工校正）。</summary>
    [JsonPropertyName("summary")] public string? Summary { get; init; }

    /// <summary>没有译文时保留的英文原文——宁可是英文，也不要编一个可能错的中文。</summary>
    [JsonPropertyName("summaryEn")] public string? SummaryEn { get; init; }

    [JsonIgnore]
    public string? Display => !string.IsNullOrWhiteSpace(Summary) ? Summary : SummaryEn;

    [JsonIgnore]
    public bool HasChinese => !string.IsNullOrWhiteSpace(Summary);
}

public sealed class AppCatalogFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("generatedAt")] public string? GeneratedAt { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("translated")] public int Translated { get; init; }
    [JsonPropertyName("entries")] public Dictionary<string, CatalogEntry> Entries { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// UAD 的 removal 结论 → 界面上的建议文案。
/// 统一放在这里，保证「建议列」和「计划引擎」用的是同一套说法。
/// </summary>
public static class RemovalAdvice
{
    public const string Recommended = "recommended";
    public const string Advanced = "advanced";
    public const string Expert = "expert";
    public const string Unsafe = "unsafe";

    public static string Normalize(string? removal) => (removal ?? string.Empty).Trim().ToLowerInvariant();

    public static string Text(string? removal) => Normalize(removal) switch
    {
        Recommended => "UAD·建议清理",
        Advanced => "UAD·可清理",
        Expert => "UAD·不建议动",
        Unsafe => "UAD·不要动",
        _ => "UAD·无结论",
    };

    /// <summary>能不能进「优化计划」：不要动的没必要列进去。</summary>
    public static bool IsActionable(string? removal, OptimizationTier tier) => Normalize(removal) switch
    {
        Recommended or Advanced => true,
        Expert => tier >= OptimizationTier.Geek,
        _ => false,
    };

    /// <summary>
    /// 默认是否勾选。
    ///
    /// 这里刻意加了一道「必须没有桌面图标」的门槛。原因是 UAD 的 Recommended 语义是
    /// 「删了不会搞坏系统、你还能从商店装回来」，而不是「这个应用是垃圾」——
    /// OEM 预装的 WPS、高德地图、小红书同样被标成 Recommended。
    /// 有桌面图标说明用户可能真的在用它，绝不能替他决定删掉。
    /// 没有图标的应用用户根本打不开，才可能是纯后台的推广/遥测组件。
    /// </summary>
    public static bool IsSelectedByDefault(string? removal, bool? hasLauncher) =>
        Normalize(removal) == Recommended && hasLauncher == false;

    /// <summary>结合「有没有桌面图标」给出最终建议文案。</summary>
    public static string TextFor(string? removal, bool? hasLauncher)
    {
        var normalized = Normalize(removal);
        // 有图标（或没读到）时降一档说法，避免用户以为程序建议删掉自己在用的应用
        if (normalized == Recommended && hasLauncher != false) return "UAD·可清理";
        return Text(normalized);
    }
}
