using System.Text.Json.Serialization;

namespace AndroidOptimize.Core.Models;

public sealed record AppOpRule
{
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
}

public sealed record SettingChoice
{
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("value")] public required string Value { get; init; }
}

/// <summary>名单里的一条记录。packages 与 settings 共用，靠 action 区分。</summary>
public sealed record RuleRecord
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("brands")] public IReadOnlyList<string> Brands { get; init; } = ["*"];
    [JsonPropertyName("category")] public string Category { get; init; } = "未分类";
    [JsonPropertyName("tier")] public OptimizationTier Tier { get; init; } = OptimizationTier.Geek;
    [JsonPropertyName("risk")] public RiskLevel Risk { get; init; } = RiskLevel.Medium;
    [JsonPropertyName("action")] public PackageAction Action { get; init; } = PackageAction.Keep;
    [JsonPropertyName("confidence")] public double Confidence { get; init; } = 0.5;
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("impact")] public string? Impact { get; init; }
    [JsonPropertyName("appOps")] public IReadOnlyList<AppOpRule>? AppOps { get; init; }
    [JsonPropertyName("standbyBucket")] public string? StandbyBucket { get; init; }

    /// <summary>
    /// 该项不受「安全模式」影响，始终按 action 执行。
    /// 目前只用于应用商店：停用可能被系统重新启用，必须真正卸载。
    /// </summary>
    [JsonPropertyName("ignoreSafetyMode")] public bool IgnoreSafetyMode { get; init; }

    // 仅 settings 使用
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    [JsonPropertyName("keys")] public IReadOnlyList<string>? Keys { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("choices")] public IReadOnlyList<SettingChoice>? Choices { get; init; }
    [JsonPropertyName("defaultChoice")] public string? DefaultChoice { get; init; }

    /// <summary>用于界面展示的完整说明。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name!;

    public bool MatchesBrand(IReadOnlyList<string> brandTokens)
    {
        foreach (var brand in Brands)
        {
            if (brand == "*") return true;
            foreach (var token in brandTokens)
            {
                if (string.Equals(token, brand, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    public bool IsSetting => Action == PackageAction.Settings && Keys is { Count: > 0 };
}

public sealed class RuleSetFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("listVersion")] public string ListVersion { get; init; } = "0.0.0";
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; init; }
    [JsonPropertyName("channel")] public string? Channel { get; init; }
    [JsonPropertyName("packages")] public IReadOnlyList<RuleRecord> Packages { get; init; } = [];
    [JsonPropertyName("settings")] public IReadOnlyList<RuleRecord> Settings { get; init; } = [];
    [JsonPropertyName("policies")] public IReadOnlyList<PolicyRecord> Policies { get; init; } = [];

    /// <summary>
    /// 「不算垃圾」的包名模式（支持 * 通配符）。
    /// 名单里查不到、但明显是正常应用（地图、健康、出行、办公等）的，不要列进「未知应用」，
    /// 免得用户一键勾选时误伤。
    /// </summary>
    [JsonPropertyName("unknownExclude")] public IReadOnlyList<string> UnknownExclude { get; init; } = [];

    /// <summary>懒人模式配置。</summary>
    [JsonPropertyName("lazyMode")] public LazyModeConfig? LazyMode { get; init; }
}

/// <summary>
/// 懒人模式：不做任何猜测，只看一份「必须保留」的白名单。
/// 白名单之外的第三方应用一律加入处理列表——这是给「给长辈收拾手机」准备的。
/// </summary>
public sealed record LazyModeConfig
{
    [JsonPropertyName("name")] public string Name { get; init; } = "懒人模式";
    [JsonPropertyName("description")] public string? Description { get; init; }

    /// <summary>
    /// 必须保留的包名模式（支持 * 通配符）。
    /// 保护名单里的应用会自动保留，不用在这里重复写。
    /// </summary>
    [JsonPropertyName("keep")] public IReadOnlyList<string> Keep { get; init; } = [];
}

/// <summary>
/// 全局策略：不针对某一个包，而是按条件作用到一批应用上。
/// 例如「除微信和地图外，关闭所有第三方应用的传感器权限」。
/// </summary>
public sealed record PolicyRecord
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("category")] public string Category { get; init; } = "策略";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("tier")] public OptimizationTier Tier { get; init; } = OptimizationTier.Normal;
    [JsonPropertyName("risk")] public RiskLevel Risk { get; init; } = RiskLevel.Low;
    [JsonPropertyName("action")] public PackageAction Action { get; init; } = PackageAction.Restrict;
    [JsonPropertyName("confidence")] public double Confidence { get; init; } = 0.8;
    [JsonPropertyName("defaultEnabled")] public bool DefaultEnabled { get; init; } = true;

    /// <summary>作用范围：thirdParty（仅第三方应用）或 all（含系统应用）。</summary>
    [JsonPropertyName("scope")] public string Scope { get; init; } = "thirdParty";

    /// <summary>排除名单，支持 * 通配符。</summary>
    [JsonPropertyName("exclude")] public IReadOnlyList<string> Exclude { get; init; } = [];

    [JsonPropertyName("appOps")] public IReadOnlyList<AppOpRule>? AppOps { get; init; }
    [JsonPropertyName("standbyBucket")] public string? StandbyBucket { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("impact")] public string? Impact { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name!;
}

public sealed record ProtectionPattern
{
    [JsonPropertyName("match")] public required string Match { get; init; }
    [JsonPropertyName("level")] public ProtectionLevel Level { get; init; } = ProtectionLevel.Absolute;
    [JsonPropertyName("category")] public string Category { get; init; } = "保护项";
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed class ProtectionFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("listVersion")] public string ListVersion { get; init; } = "0.0.0";
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; init; }
    [JsonPropertyName("patterns")] public IReadOnlyList<ProtectionPattern> Patterns { get; init; } = [];
}

public sealed record ProtectionMatch(ProtectionLevel Level, ProtectionPattern Pattern)
{
    public static readonly ProtectionMatch None = new(ProtectionLevel.None, new ProtectionPattern { Match = string.Empty });
    public bool IsProtected => Level != ProtectionLevel.None;
}
