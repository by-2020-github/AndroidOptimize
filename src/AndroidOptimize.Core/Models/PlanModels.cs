namespace AndroidOptimize.Core.Models;

public sealed class PlanItem
{
    public required string Key { get; init; }
    public required PlanItemKind Kind { get; init; }
    public required string Target { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required PackageAction Action { get; init; }
    public required RiskLevel Risk { get; init; }
    public required OptimizationTier Tier { get; init; }
    public required double Confidence { get; init; }
    public string? Reason { get; init; }
    public string? Impact { get; init; }
    public string Source { get; init; } = "list";

    /// <summary>执行前该包在设备上的状态。</summary>
    public PackagePresence PresenceBefore { get; init; } = PackagePresence.Unknown;

    /// <summary>首次安装时间，用来判断「是不是最近被莫名装上的」。</summary>
    public DateTimeOffset? InstalledAt { get; init; }

    /// <summary>安装来源包名，用来判断「是不是被商店静默装上的」。</summary>
    public string? Installer { get; init; }

    /// <summary>系统是否记录该应用「安装后从未被打开过」。null 表示设备没给出这个信息。</summary>
    public bool? NeverLaunched { get; init; }

    // 仅 Setting
    public string? SettingNamespace { get; init; }
    public IReadOnlyList<string>? SettingKeys { get; init; }
    public string? SettingValue { get; set; }
    public IReadOnlyList<SettingChoice>? Choices { get; init; }

    // 仅 Restrict
    public IReadOnlyList<AppOpRule>? AppOps { get; init; }
    public string? StandbyBucket { get; init; }

    public bool IsSelected { get; set; }
    public bool IsSelectable { get; set; } = true;

    public string ActionText => Action switch
    {
        PackageAction.Disable => "停用",
        PackageAction.Uninstall => "卸载",
        PackageAction.Restrict => "后台限制",
        PackageAction.Settings => "修改设置",
        _ => "保留",
    };

    public string RiskText => Risk switch
    {
        RiskLevel.Low => "低",
        RiskLevel.Medium => "中",
        RiskLevel.High => "高",
        _ => "?",
    };

    public string TierText => Tier switch
    {
        OptimizationTier.ScanOnly => "体检",
        OptimizationTier.Normal => "一般",
        OptimizationTier.Geek => "极客",
        OptimizationTier.Danger => "深度",
        _ => "?",
    };

    public string SourceText => Source switch
    {
        "ai" => "AI 建议",
        "list" => "名单",
        "policy" => "策略",
        "unknown" => "未知",
        _ => Source,
    };
}

public sealed record SkippedItem(string Target, string DisplayName, string Category, string Reason, ProtectionLevel ProtectionLevel);

public sealed class OptimizationPlan
{
    public required DeviceInfo Device { get; init; }
    public required OptimizationTier Tier { get; init; }
    public required IReadOnlyList<PlanItem> Items { get; init; }
    public required IReadOnlyList<SkippedItem> Skipped { get; init; }
    public required string ListVersion { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public IEnumerable<PlanItem> SelectedItems => Items.Where(i => i.IsSelected && i.IsSelectable);
    public int SelectedCount => Items.Count(i => i.IsSelected && i.IsSelectable);
    public int PackageCount => Items.Count(i => i.Kind != PlanItemKind.Setting);
    public int SettingCount => Items.Count(i => i.Kind == PlanItemKind.Setting);
    public int AiSuggestionCount => Items.Count(i => i.Kind == PlanItemKind.AiSuggestion);
    public int UnknownCount => Items.Count(i => i.Kind == PlanItemKind.Unknown);
    public IEnumerable<PlanItem> UnknownItems => Items.Where(i => i.Kind == PlanItemKind.Unknown);
    public int ProtectedCount => Skipped.Count(s => s.ProtectionLevel != ProtectionLevel.None);

    public string SelectionSummary
    {
        get
        {
            var disable = Items.Count(i => i.IsSelected && i.IsSelectable && i.Action == PackageAction.Disable);
            var uninstall = Items.Count(i => i.IsSelected && i.IsSelectable && i.Action == PackageAction.Uninstall);
            var restrict = Items.Count(i => i.IsSelected && i.IsSelectable && i.Action == PackageAction.Restrict);
            var settings = Items.Count(i => i.IsSelected && i.IsSelectable && i.Action == PackageAction.Settings);
            var parts = new List<string>();
            if (disable > 0) parts.Add($"停用 {disable} 项");
            if (uninstall > 0) parts.Add($"卸载 {uninstall} 项");
            if (restrict > 0) parts.Add($"后台限制 {restrict} 项");
            if (settings > 0) parts.Add($"设置 {settings} 项");
            return parts.Count == 0 ? "未选择任何项目" : string.Join(" · ", parts);
        }
    }
}

public sealed class PlanOptions
{
    /// <summary>安全模式：把「卸载」降级为「停用」。默认开启，对小白更友好。</summary>
    public bool SafetyMode { get; set; } = true;
    public bool IncludeSettings { get; set; } = true;
    /// <summary>是否允许处理 guarded 级别的应用（微信等），默认关闭。</summary>
    public bool AllowGuarded { get; set; }
    public string? AnimationScaleChoice { get; set; }

    /// <summary>启用的全局策略 ID 集合。不在集合里的策略不会展开。</summary>
    public IReadOnlySet<string> EnabledPolicies { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 是否把「名单外、也没有 AI 结论」的应用也列进计划（默认不勾选）。
    /// 关掉后计划里只剩名单与策略覆盖到的内容。
    /// </summary>
    public bool IncludeUnknownPackages { get; set; } = true;

    /// <summary>未知应用的处理动作。默认停用——可一键还原，效果和卸载一样。</summary>
    public PackageAction UnknownPackageAction { get; set; } = PackageAction.Disable;

    public double MinConfidence(OptimizationTier tier) => tier switch
    {
        OptimizationTier.ScanOnly => 1.01,
        OptimizationTier.Normal => 0.75,
        OptimizationTier.Geek => 0.50,
        _ => 0.0,
    };
}
