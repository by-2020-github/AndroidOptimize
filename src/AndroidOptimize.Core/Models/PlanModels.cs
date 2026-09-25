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

    /// <summary>桌面上有没有图标。null 表示没读到。</summary>
    public bool? HasLauncher { get; init; }

    /// <summary>是不是系统应用。用于列表里标「系统 / 第三方」，也用于筛选。</summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// 权限画像：这个应用申请了哪些值得警惕的权限。
    /// 只用于展示和筛选，**不参与自动勾选**。见 <see cref="PermissionAdvice"/>。
    /// </summary>
    public AppPermissionProfile PermissionProfile { get; init; } = AppPermissionProfile.Unknown;

    public string TypeText => IsSystem ? "系统" : "第三方";

    /// <summary>行悬停提示：把表格里放不下的信息补齐。</summary>
    public string RowTooltip
    {
        get
        {
            var lines = new List<string> { $"{DisplayName}（{Target}）", $"建议：{Advice.ForPlanItem(this).Text}" };
            if (!string.IsNullOrWhiteSpace(Installer)) lines.Add($"安装来源：{Installer}");
            if (InstalledAt is not null) lines.Add($"首次安装：{InstalledAt:yyyy-MM-dd}");
            if (HasActionOverride) lines.Add($"动作：{ActionText}（你手动指定的，不会按安全模式降级）");
            if (PermissionProfile.Known)
            {
                lines.Add(PermissionProfile.Hits.Count > 0
                    ? $"权限：{PermissionProfile.DisplayText}"
                    : "权限：没有命中名单里值得警惕的权限");
            }
            if (!string.IsNullOrWhiteSpace(Reason)) lines.Add(Reason!);
            if (!string.IsNullOrWhiteSpace(Impact)) lines.Add($"影响：{Impact}");
            return string.Join("\n", lines);
        }
    }

    /// <summary>离线名册给的清理结论（recommended / advanced / expert / unsafe）。</summary>
    public string? Removal { get; init; }

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

    /// <summary>
    /// 用户在「优化计划」页手动指定的动作（停用 / 卸载）。null = 用名单、名册给的动作。
    ///
    /// 这是**显式选择**：用户是在看清「会失去什么」之后自己点的，所以不再被安全模式降级
    /// （和「应用商店不受安全模式影响」是同一个道理）。原来的动作保留在 <see cref="Action"/> 里，
    /// 用户选回默认值时就把覆盖清掉。
    /// </summary>
    public PackageAction? ActionOverride { get; set; }

    /// <summary>实际会执行的动作。执行器、快照、汇总都用它，别再用 <see cref="Action"/>。</summary>
    public PackageAction EffectiveAction => ActionOverride ?? Action;

    /// <summary>用户有没有手动改过动作。</summary>
    public bool HasActionOverride => ActionOverride is not null;

    /// <summary>
    /// 能不能手动在「停用 / 卸载」之间切。设置项、后台限制（策略）这类没有这个选择，
    /// 本来就标着「保留」的也不用给。
    /// </summary>
    public bool CanChooseAction =>
        Kind is not (PlanItemKind.Setting or PlanItemKind.Policy)
        && Action is PackageAction.Disable or PackageAction.Uninstall;

    public string ActionText => EffectiveAction switch
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
        "catalog" => "名册",
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
    public int CatalogCount => Items.Count(i => i.Kind == PlanItemKind.Catalog);
    public IEnumerable<PlanItem> UnknownItems => Items.Where(i => i.Kind == PlanItemKind.Unknown);
    public int ProtectedCount => Skipped.Count(s => s.ProtectionLevel != ProtectionLevel.None);

    public string SelectionSummary
    {
        get
        {
            var disable = Items.Count(i => i.IsSelected && i.IsSelectable && i.EffectiveAction == PackageAction.Disable);
            var uninstall = Items.Count(i => i.IsSelected && i.IsSelectable && i.EffectiveAction == PackageAction.Uninstall);
            var restrict = Items.Count(i => i.IsSelected && i.IsSelectable && i.EffectiveAction == PackageAction.Restrict);
            var settings = Items.Count(i => i.IsSelected && i.IsSelectable && i.EffectiveAction == PackageAction.Settings);
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
