using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

public sealed record PlanRequest
{
    public required DeviceInfo Device { get; init; }
    public required PackageSnapshot Snapshot { get; init; }
    public required RuleRepository Rules { get; init; }
    public required OptimizationTier Tier { get; init; }
    public PlanOptions Options { get; init; } = new();
    public IReadOnlyList<PackageClassification> AiSuggestions { get; init; } = [];

    /// <summary>离线名册。为空时按「没有名册」处理，不影响其它逻辑。</summary>
    public AppCatalog? Catalog { get; init; }
}

/// <summary>
/// 纯函数式的计划生成器：不接触设备，只根据名单 + 设备状态 + 档位算出「将要做什么」。
/// 这样计划可以在执行前完整展示给用户确认，也方便单元测试。
/// </summary>
public sealed class PlanBuilder
{
    public const int DefaultUnknownLimit = 60;

    public OptimizationPlan Build(PlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var items = new List<PlanItem>();
        var skipped = new List<SkippedItem>();
        var minimumConfidence = request.Options.MinConfidence(request.Tier);
        var tokens = request.Device.BrandTokens;

        // 权限画像只用于展示与筛选，不参与任何自动勾选。
        var permissionRisks = request.Rules.RuleSet.PermissionRisks;
        var permissionCombos = request.Rules.RuleSet.PermissionCombos;
        AppPermissionProfile ProfileOf(PackageEntry? entry) =>
            PermissionAdvice.Build(entry?.Permissions, permissionRisks, permissionCombos);

        // 只记录「已经有 AI 结论」的包：这些不该再被当成未知应用重复列一次。
        // 注意策略（如传感器权限）不算——它只限制了某个权限，并没有说明这个应用是什么。
        var aiCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先把全局策略展开成「哪些包要额外下发哪些限制」，再和名单规则合并，
        // 这样同一个应用只出现一行，而不是被名单和策略各列一次。
        var policies = ExpandPolicies(request, minimumConfidence, skipped, tokens);

        foreach (var rule in request.Rules.RuleSet.Packages)
        {
            if (rule.Tier > request.Tier)
            {
                continue;
            }

            // 保护名单优先于一切：即使这条记录标成 keep，也要把它「为什么不动」讲清楚。
            var protection = request.Rules.MatchProtection(rule.Id);
            if (protection.Level == ProtectionLevel.Absolute)
            {
                skipped.Add(new SkippedItem(rule.Id, rule.DisplayName, rule.Category,
                    $"受保护，不会被处理：{protection.Pattern.Reason}", protection.Level));
                continue;
            }

            if (protection.Level == ProtectionLevel.Guarded && !request.Options.AllowGuarded)
            {
                skipped.Add(new SkippedItem(rule.Id, rule.DisplayName, rule.Category,
                    $"需要手动解锁「允许操作受保护应用」后才会出现：{protection.Pattern.Reason}", protection.Level));
                continue;
            }

            if (rule.Action == PackageAction.Keep || rule.IsSetting)
            {
                continue;
            }

            if (!rule.MatchesBrand(tokens))
            {
                continue;
            }

            if (rule.Confidence < minimumConfidence)
            {
                skipped.Add(new SkippedItem(rule.Id, rule.DisplayName, rule.Category,
                    $"证据不足：名单置信度 {rule.Confidence:0.00} 低于当前档位要求的 {minimumConfidence:0.00}，默认不处理。", ProtectionLevel.None));
                continue;
            }

            var entry = request.Snapshot.Find(rule.Id);
            var presence = entry?.Presence ?? PackagePresence.RemovedForUser;

            if (presence == PackagePresence.RemovedForUser)
            {
                skipped.Add(new SkippedItem(rule.Id, rule.DisplayName, rule.Category,
                    "当前设备上未安装（可能此前已经处理过）。", ProtectionLevel.None));
                continue;
            }

            if (rule.Action == PackageAction.Disable && presence == PackagePresence.Disabled)
            {
                skipped.Add(new SkippedItem(rule.Id, rule.DisplayName, rule.Category,
                    "已经是停用状态，无需重复处理。", ProtectionLevel.None));
                continue;
            }

            var effectiveAction = rule.Action;
            var downgraded = false;
            if (effectiveAction == PackageAction.Uninstall && request.Options.SafetyMode && !rule.IgnoreSafetyMode)
            {
                effectiveAction = PackageAction.Disable;
                downgraded = true;
            }

            var impact = rule.Impact;
            if (downgraded)
            {
                impact = string.IsNullOrWhiteSpace(impact) ? "安全模式下以「停用」代替「卸载」。"
                    : $"{impact}（安全模式下以「停用」代替「卸载」）";
            }

            // 只有「后台限制」类项目才需要合并策略下发的 appOps；
            // 会被停用或卸载的应用，传感器权限已经没有意义。
            var mergedOps = effectiveAction == PackageAction.Restrict
                ? MergeAppOps(rule.AppOps, policies.OpsFor(rule.Id))
                : rule.AppOps;

            if (effectiveAction == PackageAction.Restrict && policies.Has(rule.Id))
            {
                impact = AppendPolicyNote(impact, policies.PoliciesFor(rule.Id));
            }

            items.Add(new PlanItem
            {
                Key = rule.Id,
                Kind = PlanItemKind.Package,
                Target = rule.Id,
                DisplayName = rule.DisplayName,
                Category = rule.Category,
                Action = effectiveAction,
                Risk = rule.Risk,
                Tier = rule.Tier,
                Confidence = rule.Confidence,
                Reason = rule.Reason,
                Impact = impact,
                Source = "list",
                PresenceBefore = presence,
                IsSystem = entry?.IsSystem ?? false,
                PermissionProfile = ProfileOf(entry),
                AppOps = mergedOps,
                StandbyBucket = rule.StandbyBucket,
                IsSelected = DefaultSelected(rule, request.Tier, isSetting: false),
            });
            policies.MarkCovered(rule.Id);
        }

        // 只被策略命中、名单里没有专门规则的应用，单独成行。
        foreach (var (packageName, ops) in policies.UncoveredPackages())
        {
            var entry = request.Snapshot.Find(packageName);
            if (entry is null || !entry.Installed) continue;

            var policyList = policies.PoliciesFor(packageName);
            var primary = policyList[0];

            items.Add(new PlanItem
            {
                Key = "policy:" + primary.Id + ":" + packageName,
                Kind = PlanItemKind.Policy,
                Target = packageName,
                // 和别的条目一样优先用读到的应用名——只显示包名的话，用户根本认不出这是哪个应用。
                DisplayName = entry.Label ?? packageName,
                Category = primary.Category,
                Action = PackageAction.Restrict,
                Risk = primary.Risk,
                Tier = primary.Tier,
                Confidence = primary.Confidence,
                Reason = primary.Reason,
                Impact = primary.Impact,
                Source = "policy",
                PresenceBefore = entry.Presence,
                IsSystem = entry.IsSystem,
                PermissionProfile = ProfileOf(entry),
                AppOps = ops,
                StandbyBucket = primary.StandbyBucket,
                IsSelected = primary.Risk != RiskLevel.High,
            });
        }

        if (request.Options.IncludeSettings)
        {
            foreach (var rule in request.Rules.RuleSet.Settings)
            {
                if (rule.Tier > request.Tier || !rule.IsSetting) continue;

                var protection = request.Rules.MatchProtection($"setting:{rule.Id}");
                if (protection.Level == ProtectionLevel.Absolute) continue;

                if (!rule.MatchesBrand(tokens)) continue;

                if (rule.Confidence < minimumConfidence)
                {
                    skipped.Add(new SkippedItem(rule.Id, rule.DisplayName, rule.Category,
                        $"证据不足：置信度 {rule.Confidence:0.00} 低于当前档位要求。", ProtectionLevel.None));
                    continue;
                }

                var value = rule.Value ?? rule.DefaultChoice;
                if (rule.Choices is { Count: > 0 })
                {
                    value = request.Options.AnimationScaleChoice
                            ?? rule.DefaultChoice
                            ?? rule.Choices[0].Value;
                }

                items.Add(new PlanItem
                {
                    Key = rule.Id,
                    Kind = PlanItemKind.Setting,
                    Target = rule.Id,
                    DisplayName = rule.DisplayName,
                    Category = rule.Category,
                    Action = PackageAction.Settings,
                    Risk = rule.Risk,
                    Tier = rule.Tier,
                    Confidence = rule.Confidence,
                    Reason = rule.Reason,
                    Impact = rule.Impact,
                    Source = "list",
                    SettingNamespace = rule.Namespace ?? "global",
                    SettingKeys = rule.Keys,
                    SettingValue = value,
                    Choices = rule.Choices,
                    IsSelected = DefaultSelected(rule, request.Tier, isSetting: true),
                });
            }
        }

        foreach (var suggestion in request.AiSuggestions)
        {
            if (suggestion.Action is PackageAction.Keep)
            {
                continue;
            }

            if (request.Rules.MatchProtection(suggestion.PackageName).IsProtected)
            {
                continue;
            }

            if (request.Rules.FindPackageRule(suggestion.PackageName, tokens) is not null)
            {
                continue;
            }

            var entry = request.Snapshot.Find(suggestion.PackageName);
            if (entry is null || !entry.Installed)
            {
                continue;
            }

            var action = suggestion.Action;
            if (action == PackageAction.Uninstall && request.Options.SafetyMode)
            {
                action = PackageAction.Disable;
            }

            items.Add(new PlanItem
            {
                Key = "ai:" + suggestion.PackageName,
                Kind = PlanItemKind.AiSuggestion,
                Target = suggestion.PackageName,
                DisplayName = entry.Label ?? suggestion.PackageName,
                Category = suggestion.Category,
                Action = action,
                Risk = suggestion.Confidence >= 0.9 ? RiskLevel.Low : RiskLevel.Medium,
                Tier = request.Tier,
                Confidence = suggestion.Confidence,
                Reason = suggestion.Reason,
                Impact = "这是自动识别结果，未经人工复核。请确认这个应用对使用者无用后再勾选。",
                Source = suggestion.Source,
                PresenceBefore = entry.Presence,
                IsSystem = entry.IsSystem,
                PermissionProfile = ProfileOf(entry),
                InstalledAt = entry.FirstInstallTime,
                Installer = entry.Installer,
                IsSelected = false, // AI 建议永远默认不勾选
            });
            aiCovered.Add(suggestion.PackageName);
        }

        // 名单里没有、也没有 AI 结论的第三方应用。
        // 这正是「老人手机里莫名多出一堆应用」的主要来源：程序不知道它们是什么，
        // 所以默认一律不勾选，只在用户主动点「选中未知应用」时才处理。
        if (request.Options.IncludeUnknownPackages && request.Options.UnknownPackageAction != PackageAction.Keep)
        {
            foreach (var entry in request.Snapshot.InstalledPackages)
            {
                if (aiCovered.Contains(entry.Name)) continue;
                if (request.Rules.MatchProtection(entry.Name).IsProtected) continue;
                // 名单里有记录的（哪怕这条规则当前档位不处理、或置信度不够）都不算「未知」——
                // 程序知道它是什么，只是这一档不动它。
                if (request.Rules.FindPackageRule(entry.Name, tokens) is not null) continue;

                var catalogEntry = request.Catalog?.Find(entry.Name);
                if (catalogEntry is not null)
                {
                    // 名册里有资料。只有社区明确说「可以安全移除」的才默认勾选；
                    // expert / unsafe 不进计划——它们是「不要动」，列进来只会造成困扰。
                    if (!RemovalAdvice.IsActionable(catalogEntry.Removal, request.Tier)) continue;

                    items.Add(new PlanItem
                    {
                        Key = "catalog:" + entry.Name,
                        Kind = PlanItemKind.Catalog,
                        Target = entry.Name,
                        DisplayName = entry.Label ?? entry.Name,
                        Category = "名册收录",
                        Action = request.Options.UnknownPackageAction,
                        Risk = RiskFromRemoval(catalogEntry.Removal),
                        Tier = request.Tier,
                        Confidence = RemovalAdvice.Normalize(catalogEntry.Removal) == RemovalAdvice.Recommended ? 0.85 : 0.6,
                        Reason = catalogEntry.Display,
                        Impact = BuildCatalogImpact(catalogEntry.Removal, entry.HasLauncher),
                        Source = "catalog",
                        PresenceBefore = entry.Presence,
                        IsSystem = entry.IsSystem,
                        PermissionProfile = ProfileOf(entry),
                        InstalledAt = entry.FirstInstallTime,
                        Installer = entry.Installer,
                        NeverLaunched = entry.NeverLaunched,
                        HasLauncher = entry.HasLauncher,
                        Removal = catalogEntry.Removal,
                        IsSelected = RemovalAdvice.IsSelectedByDefault(catalogEntry.Removal, entry.HasLauncher),
                    });
                    continue;
                }

                // 名册里也没有：只有第三方应用才值得列出来（系统应用没资料就不要碰）。
                if (!entry.IsThirdParty) continue;
                items.Add(new PlanItem
                {
                    Key = "unknown:" + entry.Name,
                    Kind = PlanItemKind.Unknown,
                    Target = entry.Name,
                    DisplayName = entry.Label ?? entry.Name,
                    Category = "未知应用",
                    Action = request.Options.UnknownPackageAction,
                    Risk = RiskLevel.Medium,
                    Tier = request.Tier,
                    Confidence = 0.3,
                    Reason = DescribeUnknown(entry),
                    Impact = "名单和离线名册里都没有这个应用，程序无法判断它的用途。默认用「停用」而不是「卸载」——"
                             + "停用后它不能再运行、不会再弹广告，而且随时可以在「回滚」页一键还原。",
                    Source = "unknown",
                    PresenceBefore = entry.Presence,
                    IsSystem = entry.IsSystem,
                    PermissionProfile = ProfileOf(entry),
                    InstalledAt = entry.FirstInstallTime,
                    Installer = entry.Installer,
                    NeverLaunched = entry.NeverLaunched,
                    HasLauncher = entry.HasLauncher,
                    IsSelected = false,
                });
            }
        }

        var ordered = items
            // 名册条目紧跟名单条目：它们是「扫完就会被勾上」的主要内容，必须显眼。
            .OrderBy(i => i.Kind switch
            {
                PlanItemKind.Package => 0,
                PlanItemKind.Catalog => 1,
                PlanItemKind.Setting => 2,
                PlanItemKind.Policy => 3,
                PlanItemKind.AiSuggestion => 4,
                _ => 5,
            })
            // 未知应用排序：先「从未被打开过」的（最像垃圾），再按安装时间倒序（最近装的更可疑）。
            .ThenByDescending(i => i.Kind == PlanItemKind.Unknown ? (i.NeverLaunched == true ? 1 : 0) : 0)
            .ThenByDescending(i => i.Kind == PlanItemKind.Unknown ? i.InstalledAt : null)
            .ThenBy(i => i.Category)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        return new OptimizationPlan
        {
            Device = request.Device,
            Tier = request.Tier,
            Items = ordered,
            Skipped = skipped,
            ListVersion = request.Rules.ListVersion,
            Warnings = request.Rules.Warnings.Concat(request.Snapshot.Warnings).ToList(),
        };
    }

    /// <summary>把「安装来源 + 安装时间」摆到明面上——这是判断该不该处理的主要依据。</summary>
    private static string DescribeUnknown(PackageEntry entry)
    {
        var parts = new List<string> { "名单里没有这个应用，程序无法判断它的用途。" };

        if (!string.IsNullOrWhiteSpace(entry.Installer))
        {
            parts.Add($"安装来源：{entry.Installer}");
        }

        if (entry.FirstInstallTime is not null)
        {
            var days = (DateTimeOffset.Now - entry.FirstInstallTime.Value).TotalDays;
            var when = days < 1 ? "今天"
                : days < 30 ? $"{(int)days} 天前"
                : entry.FirstInstallTime.Value.ToString("yyyy-MM-dd");
            parts.Add($"首次安装：{entry.FirstInstallTime.Value:yyyy-MM-dd}（{when}）");
        }

        parts.Add(entry.NeverLaunched switch
        {
            // 这个字段只说明「有没有被启动过」，不区分是用户点开的还是被别的应用唤醒的，
            // 所以措辞上不能替用户下结论。
            true => "系统记录：装完之后从未被启动过。",
            false => "系统记录：被启动过（可能只是被其它应用唤醒）。",
            _ => "系统没有提供启动记录。",
        });

        parts.Add("仅供参考，请自己确认它是不是有用的应用。");
        return string.Join("  ", parts);
    }

    private static RiskLevel RiskFromRemoval(string? removal) => RemovalAdvice.Normalize(removal) switch
    {
        RemovalAdvice.Recommended => RiskLevel.Low,
        RemovalAdvice.Advanced => RiskLevel.Medium,
        _ => RiskLevel.High,
    };

    private static string BuildCatalogImpact(string? removal, bool? hasLauncher)
    {
        var normalized = RemovalAdvice.Normalize(removal);
        const string attribution = "结论来自 UAD（Universal Android Debloater）社区，不是本项目的判断。";

        if (normalized == RemovalAdvice.Recommended)
        {
            return hasLauncher == false
                ? $"{attribution}这个应用在桌面上没有图标，用户打不开它，通常是后台的推广或统计组件。"
                  + "处理方式是停用：不能运行、随时可在「回滚」页还原。"
                : $"{attribution}它在桌面上有图标，使用者可能在用，所以默认不勾选——请自行确认。";
        }

        return normalized == RemovalAdvice.Advanced
            ? $"{attribution}社区认为可以移除，但需要先确认：可能有依赖它的功能会受影响。"
            : $"{attribution}社区不建议普通用户移除。";
    }
    /// <summary>
    /// 挑出需要识别的未知应用：已安装的第三方应用、名单里没有记录、且不在保护名单里。
    /// 优先挑最近安装的（越近越可能是被强行装上的）。
    /// </summary>
    public IReadOnlyList<UnknownPackage> SelectUnknownTargets(
        PackageSnapshot snapshot,
        RuleRepository rules,
        int limit = DefaultUnknownLimit)
    {
        var tokens = snapshot.Device.BrandTokens;
        var result = new List<UnknownPackage>();

        foreach (var entry in snapshot.InstalledPackages)
        {
            if (entry.IsSystem) continue;
            if (rules.FindPackageRule(entry.Name, tokens) is not null) continue;
            if (rules.MatchProtection(entry.Name).IsProtected) continue;

            result.Add(new UnknownPackage
            {
                PackageName = entry.Name,
                Installer = entry.Installer,
                VersionName = entry.VersionName,
                FirstInstallTime = entry.FirstInstallTime,
                IsSystem = entry.IsSystem,
            });
        }

        return result
            .OrderByDescending(u => u.FirstInstallTime ?? DateTimeOffset.MinValue)
            .ThenBy(u => u.PackageName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static bool DefaultSelected(RuleRecord rule, OptimizationTier tier, bool isSetting)
    {
        if (rule.Risk == RiskLevel.High) return false;
        if (isSetting && rule.Risk != RiskLevel.Low) return false;
        if (rule.Tier == OptimizationTier.Danger) return false;
        return tier > OptimizationTier.ScanOnly;
    }

    /// <summary>把启用的全局策略展开成「包名 → 需要下发的 appOps」。</summary>
    private static PolicyExpansion ExpandPolicies(
        PlanRequest request,
        double minimumConfidence,
        List<SkippedItem> skipped,
        IReadOnlyList<string> brandTokens)
    {
        var expansion = new PolicyExpansion();
        if (request.Tier <= OptimizationTier.ScanOnly) return expansion;

        foreach (var policy in request.Rules.Policies)
        {
            if (policy.Tier > request.Tier) continue;
            if (!request.Options.EnabledPolicies.Contains(policy.Id)) continue;

            if (policy.Confidence < minimumConfidence)
            {
                skipped.Add(new SkippedItem(policy.Id, policy.DisplayName, policy.Category,
                    $"证据不足：置信度 {policy.Confidence:0.00} 低于当前档位要求。", ProtectionLevel.None));
                continue;
            }

            var hasWork = policy.AppOps is { Count: > 0 } || !string.IsNullOrWhiteSpace(policy.StandbyBucket);
            if (!hasWork) continue;

            var ruleCount = 0;
            foreach (var entry in request.Snapshot.InstalledPackages)
            {
                if (string.Equals(policy.Scope, "thirdParty", StringComparison.OrdinalIgnoreCase) && !entry.IsThirdParty)
                {
                    continue;
                }

                // 保护名单优先：微信、QQ 这些即使在排除名单里写漏了也绝不会被策略碰到。
                if (request.Rules.MatchProtection(entry.Name).IsProtected) continue;
                if (request.Rules.IsExcludedByPolicy(policy, entry.Name)) continue;

                expansion.Add(entry.Name, policy);
                ruleCount++;
            }

            if (ruleCount > 0)
            {
                expansion.RecordPolicy(policy);
            }
        }

        return expansion;
    }

    private static IReadOnlyList<AppOpRule>? MergeAppOps(
        IReadOnlyList<AppOpRule>? original,
        IReadOnlyList<AppOpRule> extra)
    {
        if (extra.Count == 0) return original;

        var merged = new List<AppOpRule>(original ?? []);
        foreach (var op in extra)
        {
            if (!merged.Any(existing => string.Equals(existing.Op, op.Op, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(op);
            }
        }
        return merged;
    }

    private static string? AppendPolicyNote(string? impact, IReadOnlyList<PolicyRecord> policies)
    {
        if (policies.Count == 0) return impact;
        var names = string.Join("、", policies.Select(p => p.DisplayName));
        return string.IsNullOrWhiteSpace(impact) ? $"同时应用策略：{names}。" : $"{impact} 同时应用策略：{names}。";
    }

    /// <summary>策略展开的中间结果。</summary>
    private sealed class PolicyExpansion
    {
        private readonly Dictionary<string, List<AppOpRule>> _ops = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<PolicyRecord>> _policies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PolicyRecord> _primary = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _covered = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PolicyRecord> _used = [];

        public void Add(string packageName, PolicyRecord policy)
        {
            if (!_ops.TryGetValue(packageName, out var ops))
            {
                ops = [];
                _ops[packageName] = ops;
                _primary[packageName] = policy;
            }

            foreach (var op in policy.AppOps ?? [])
            {
                if (!ops.Any(existing => string.Equals(existing.Op, op.Op, StringComparison.OrdinalIgnoreCase)))
                {
                    ops.Add(op);
                }
            }

            if (!_policies.TryGetValue(packageName, out var list))
            {
                list = [];
                _policies[packageName] = list;
            }
            list.Add(policy);
        }

        public void RecordPolicy(PolicyRecord policy)
        {
            if (!_used.Contains(policy)) _used.Add(policy);
        }

        public IReadOnlyList<PolicyRecord> UsedPolicies => _used;
        public bool Has(string packageName) => _ops.ContainsKey(packageName);
        public IReadOnlyList<AppOpRule> OpsFor(string packageName) =>
            _ops.TryGetValue(packageName, out var ops) ? ops : [];

        public IReadOnlyList<PolicyRecord> PoliciesFor(string packageName) =>
            _policies.TryGetValue(packageName, out var list) ? list : [];

        public PolicyRecord PrimaryFor(string packageName) =>
            _primary.TryGetValue(packageName, out var policy) ? policy : throw new InvalidOperationException(packageName);

        public void MarkCovered(string packageName) => _covered.Add(packageName);

        public IEnumerable<(string PackageName, IReadOnlyList<AppOpRule> Ops)> UncoveredPackages()
        {
            foreach (var (packageName, ops) in _ops)
            {
                if (_covered.Contains(packageName)) continue;
                yield return (packageName, ops);
            }
        }
    }
}
