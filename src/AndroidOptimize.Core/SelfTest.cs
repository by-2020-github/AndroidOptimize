using System.IO.Compression;
using System.Text;
using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Apk;
using AndroidOptimize.Core.Models;
using AndroidOptimize.Core.Services;
using AndroidOptimize.Core.Testing;

namespace AndroidOptimize.Core;

/// <summary>
/// 不依赖真机的自检：验证名单加载、保护规则、档位门控、安全模式降级、AI 建议门控。
/// 用 --selftest 运行，或由 CI 调用。
/// </summary>
public static class SelfTest
{
    /// <summary>自检用的断言回调。用自定义委托是为了保留 detail 的默认值。</summary>
    private delegate void CheckReporter(string name, bool condition, string? detail = null);

    public static string Run(string? appBaseDirectory = null)
    {
        var text = new StringBuilder();
        var failures = new List<string>();
        var checks = 0;

        void Check(string name, bool condition, string? detail = null)
        {
            checks++;
            if (condition)
            {
                text.AppendLine($"  [通过] {name}");
            }
            else
            {
                failures.Add(name);
                text.AppendLine($"  [失败] {name}{(detail is null ? "" : " —— " + detail)}");
            }
        }

        text.AppendLine("AndroidOptimize 自检");
        text.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"用户数据目录：{AppPaths.UserRoot}");
        text.AppendLine();

        // ---------- 1. ADB ----------
        text.AppendLine("[1] ADB 定位与内置释放");
        var bootstrapped = AdbBootstrap.Ensure();
        Check("内置 adb 已释放到用户目录", bootstrapped is not null && File.Exists(bootstrapped), bootstrapped ?? "(null)");

        var secondRun = AdbBootstrap.Ensure();
        Check("重复释放不会重复写盘（幂等）", string.Equals(bootstrapped, secondRun, StringComparison.OrdinalIgnoreCase));

        var adb = AdbLocator.Locate(appBaseDirectory: appBaseDirectory);
        Check("找到 adb.exe", adb is not null, string.Join(" / ", AdbLocator.Candidates(appBaseDirectory: appBaseDirectory).Select(c => c.Source)));
        if (adb is not null)
        {
            text.AppendLine($"  路径：{adb.Path}");
            text.AppendLine($"  来源：{adb.Source}");
        }
        text.AppendLine();

        // ---------- 2. 名单加载 ----------
        text.AppendLine("[2] 名单加载");
        var rules = RuleRepository.Load(appBaseDirectory);
        Check("应用规则数量 > 50", rules.PackageRuleCount > 50, $"实际 {rules.PackageRuleCount}");
        Check("设置规则数量 >= 2", rules.SettingRuleCount >= 2, $"实际 {rules.SettingRuleCount}");
        Check("保护规则数量 > 50", rules.ProtectionRuleCount > 50, $"实际 {rules.ProtectionRuleCount}");
        Check("名单来源为内置或程序目录", rules.RuleSource is ListSource.Builtin or ListSource.AppFolder, rules.RuleSource.ToString());
        text.AppendLine($"  {rules.SourceDescription}");
        foreach (var warning in rules.Warnings)
        {
            text.AppendLine($"  提示：{warning}");
        }
        text.AppendLine();

        // ---------- 3. 保护名单 ----------
        text.AppendLine("[3] 保护名单");
        Check("系统界面受保护", rules.MatchProtection("com.android.systemui").Level == ProtectionLevel.Absolute);
        Check("设置受保护", rules.MatchProtection("com.android.settings").Level == ProtectionLevel.Absolute);
        Check("输入法受保护（通配符）", rules.MatchProtection("com.sohu.inputmethod.sogou").Level == ProtectionLevel.Absolute);
        Check("联系人提供者受保护（通配符）", rules.MatchProtection("com.android.providers.contacts").Level == ProtectionLevel.Absolute);
        Check("反诈中心受保护", rules.MatchProtection("com.hicorenational.antifraud").Level == ProtectionLevel.Absolute);
        Check("安全守护受保护", rules.MatchProtection("com.miui.guardprovider").Level == ProtectionLevel.Absolute);
        Check("手机管家受保护", rules.MatchProtection("com.miui.securitycenter").Level == ProtectionLevel.Absolute);
        Check("银行类受保护（模糊匹配）", rules.MatchProtection("com.icbc.mobilebank").Level == ProtectionLevel.Absolute);
        Check("微信为 guarded", rules.MatchProtection("com.tencent.mm").Level == ProtectionLevel.Guarded);
        Check("广告 SDK 不受保护", rules.MatchProtection("com.miui.systemAdSolution").Level == ProtectionLevel.None);
        text.AppendLine();

        // ---------- 4. 计划生成 ----------
        text.AppendLine("[4] 计划生成");
        var device = BuildSyntheticDevice();
        var snapshot = BuildSyntheticSnapshot(device);
        var builder = new PlanBuilder();

        var aiSuggestions = new List<PackageClassification>
        {
            new()
            {
                PackageName = "com.unknown.adapp", Category = "广告与跟踪", Action = PackageAction.Disable,
                Confidence = 0.92, Reason = "疑似推广类应用",
            },
            new()
            {
                PackageName = "com.miui.guardprovider", Category = "系统组件", Action = PackageAction.Uninstall,
                Confidence = 0.99, Reason = "AI 误判：试图删除受保护组件",
            },
            new()
            {
                PackageName = "com.miui.systemAdSolution", Category = "广告与跟踪", Action = PackageAction.Disable,
                Confidence = 0.9, Reason = "名单里已有记录，应被忽略",
            },
        };

        var normalPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = true },
            AiSuggestions = aiSuggestions,
        });

        var normalTargets = normalPlan.Items.Select(i => i.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check("一般优化包含应用商店", normalTargets.Contains("com.xiaomi.market"));
        Check("一般优化包含快应用框架", normalTargets.Contains("com.miui.hybrid"));
        Check("一般优化包含系统广告服务", normalTargets.Contains("com.miui.systemAdSolution"));
        Check("一般优化不含极客项（小爱同学）", !normalTargets.Contains("com.miui.voiceassist"));
        Check("一般优化不含系统更新", !normalTargets.Contains("com.android.updater"));
        Check("受保护组件被排除", !normalTargets.Contains("com.miui.guardprovider"));
        Check("输入法被排除", !normalTargets.Contains("com.baidu.input_mi"));
        Check("安全模式下卸载降级为停用",
            normalPlan.Items.Where(i => i.Target == "com.android.browser").All(i => i.Action == PackageAction.Disable));
        Check("跳过列表记录了受保护项",
            normalPlan.Skipped.Any(s => s.Target == "com.miui.guardprovider" && s.ProtectionLevel == ProtectionLevel.Absolute));
        Check("AI 建议默认不勾选",
            normalPlan.Items.Where(i => i.Kind == PlanItemKind.AiSuggestion).All(i => !i.IsSelected));
        Check("AI 不得建议删除受保护组件",
            !normalPlan.Items.Any(i => i.Kind == PlanItemKind.AiSuggestion && i.Target == "com.miui.guardprovider"));
        Check("AI 不重复建议名单已有的包",
            !normalPlan.Items.Any(i => i.Kind == PlanItemKind.AiSuggestion && i.Target == "com.miui.systemAdSolution"));
        Check("AI 建议生成了未知名应用条目",
            normalPlan.Items.Any(i => i.Kind == PlanItemKind.AiSuggestion && i.Target == "com.unknown.adapp"));
        Check("一般优化包含动画设置项", normalTargets.Contains("global.animation_scale"));
        Check("动画设置项默认勾选",
            normalPlan.Items.Where(i => i.Target == "global.animation_scale").All(i => i.IsSelected));
        Check("应用商店在安全模式下仍然卸载（不被降级）",
            normalPlan.Items.Where(i => i.Target == "com.xiaomi.market").All(i => i.Action == PackageAction.Uninstall));
        text.AppendLine($"  一般优化：{normalPlan.SelectionSummary}；跳过 {normalPlan.Skipped.Count} 项（其中受保护 {normalPlan.ProtectedCount} 项）");

        // ---------- 4b. 全局策略：传感器权限 ----------
        text.AppendLine();
        text.AppendLine("[4b] 全局策略（传感器权限 / 防摇一摇广告）");
        Check("名单里存在传感器策略", rules.Policies.Any(p => p.Id == "sensor_lock"),
            string.Join(",", rules.Policies.Select(p => p.Id)));

        var policyPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions
            {
                SafetyMode = true,
                IncludeSettings = true,
                EnabledPolicies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sensor_lock" },
            },
        });

        var policyItems = policyPlan.Items.Where(i => i.Kind == PlanItemKind.Policy).ToList();
        Check("策略为名单外应用生成了条目",
            policyItems.Any(i => i.Target == "com.unknown.adapp"));
        Check("策略条目带上了传感器 appOps",
            policyItems.FirstOrDefault(i => i.Target == "com.unknown.adapp")?.AppOps?.Any(op => op.Op == "BODY_SENSORS") == true);
        Check("策略条目不动后台分桶（只关传感器）",
            policyItems.All(i => string.IsNullOrWhiteSpace(i.StandbyBucket)));
        Check("策略默认勾选", policyItems.All(i => i.IsSelected));
        Check("微信被策略排除", policyItems.All(i => i.Target != "com.tencent.mm"));
        Check("地图类应用被策略排除", policyItems.All(i => i.Target != "com.autonavi.minimap"));
        Check("运动健康类应用被策略排除", policyItems.All(i => i.Target != "com.huawei.health"));
        Check("系统应用不在策略范围内",
            policyItems.All(i => i.Target != "com.miui.systemAdSolution" && i.Target != "com.android.systemui"));
        Check("受保护的应用不会被策略碰到",
            policyItems.All(i => !rules.MatchProtection(i.Target).IsProtected));
        Check("策略条目来源标记为 policy", policyItems.All(i => i.Source == "policy"));

        var policyOffPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = true },
        });
        Check("关闭策略后不再生成策略条目",
            !policyOffPlan.Items.Any(i => i.Kind == PlanItemKind.Policy));

        var geekPolicyPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Geek,
            Options = new PlanOptions
            {
                SafetyMode = true,
                IncludeSettings = true,
                EnabledPolicies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sensor_lock" },
            },
        });
        var taobaoItems = geekPolicyPlan.Items.Where(i => i.Target == "com.taobao.taobao").ToList();
        Check("已有后台限制的应用不会重复出行", taobaoItems.Count == 1, $"实际 {taobaoItems.Count} 行");
        Check("后台限制与传感器限制被合并到同一行",
            taobaoItems.Count == 1
            && taobaoItems[0].AppOps?.Any(op => op.Op == "WAKE_LOCK") == true
            && taobaoItems[0].AppOps?.Any(op => op.Op == "BODY_SENSORS") == true);
        text.AppendLine($"  传感器策略覆盖 {policyItems.Count} 个应用；极客档合并后共 {geekPolicyPlan.Items.Count} 项");

        // ---------- 4c. 未知应用一键清理 ----------
        text.AppendLine();
        text.AppendLine("[4c] 未知应用（没 API Key 也能处理「莫名装上的应用」）");

        var unknownPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = true, IncludeUnknownPackages = true },
        });

        var unknownItems = unknownPlan.UnknownItems.ToList();
        var unknownTargets = unknownItems.Select(i => i.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check("未知应用被列进计划",
            unknownTargets.Contains("com.unknown.adapp") && unknownTargets.Contains("com.unknown.helper"),
            string.Join(",", unknownTargets));
        Check("未知应用默认不勾选", unknownItems.All(i => !i.IsSelected));
        Check("未知应用默认用停用而不是卸载",
            unknownItems.All(i => i.Action == PackageAction.Disable));
        Check("未知应用标注了安装来源与安装时间",
            unknownItems.All(i => i.Reason is not null && i.Reason.Contains("安装来源")),
            unknownItems.FirstOrDefault()?.Reason ?? "(空)");
        Check("微信（受保护）不会出现在未知应用里", !unknownTargets.Contains("com.tencent.mm"));
        Check("名单里有记录的应用不算未知（哪怕当前档位不处理）",
            !unknownTargets.Contains("com.ss.android.ugc.aweme"));
        // 名册里没有资料的系统应用不进计划——没依据就不要碰
        Check("系统应用不会出现在未知应用里",
            unknownItems.All(i => i.Target != "com.miui.systemAdSolution" && i.Target != "com.android.systemui"));
        Check("记录到了「从未打开过」标志",
            unknownItems.FirstOrDefault(i => i.Target == "com.unknown.adapp")?.NeverLaunched == true);
        Check("「从未打开过」的应用排在前面",
            unknownItems.FindIndex(i => i.NeverLaunched == true)
            < unknownItems.FindIndex(i => i.NeverLaunched != true),
            string.Join(",", unknownItems.Select(i => $"{i.Target}:{i.NeverLaunched}")));
        Check("说明里写清了系统的启动记录",
            unknownItems.Any(i => i.Reason?.Contains("从未被启动过") == true)
            && unknownItems.Any(i => i.Reason?.Contains("被启动过") == true));

        var noUnknownPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = true, IncludeUnknownPackages = false },
        });
        Check("关掉开关后不再列出未知应用", noUnknownPlan.UnknownCount == 0);
        text.AppendLine($"  列出 {unknownItems.Count} 个未知应用：{string.Join("、", unknownTargets)}");

        // ---------- 4d. 离线名册（作用 + 该不该动） ----------
        text.AppendLine();
        text.AppendLine("[4d] 离线名册（UAD 社区资料 → 建议与说明）");

        Check("removal → 建议文案映射正确",
            RemovalAdvice.Text("Recommended") == "UAD·建议清理"
            && RemovalAdvice.Text("Advanced") == "UAD·可清理"
            && RemovalAdvice.Text("Expert") == "UAD·不建议动"
            && RemovalAdvice.Text("Unsafe") == "UAD·不要动");
        Check("Recommended + 没有桌面图标才默认勾选",
            RemovalAdvice.IsSelectedByDefault("Recommended", hasLauncher: false)
            && !RemovalAdvice.IsSelectedByDefault("Recommended", hasLauncher: true)
            && !RemovalAdvice.IsSelectedByDefault("Recommended", hasLauncher: null)
            && !RemovalAdvice.IsSelectedByDefault("Advanced", hasLauncher: false));
        Check("有桌面图标的 Recommended 降级成「可清理」文案",
            RemovalAdvice.TextFor("Recommended", hasLauncher: true) == "UAD·可清理"
            && RemovalAdvice.TextFor("Recommended", hasLauncher: false) == "UAD·建议清理");
        Check("Expert 只在极客档及以上可处理",
            !RemovalAdvice.IsActionable("Expert", OptimizationTier.Normal)
            && RemovalAdvice.IsActionable("Expert", OptimizationTier.Geek));
        Check("Unsafe 任何档位都不进计划",
            !RemovalAdvice.IsActionable("Unsafe", OptimizationTier.Danger));

        var testCatalog = AppCatalog.FromEntries(new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["com.demo.recommended"] = new() { Removal = "recommended", Summary = "演示用：推荐清理" },
            ["com.demo.recommended.visible"] = new() { Removal = "recommended", Summary = "演示用：有桌面图标" },
            ["com.demo.advanced"] = new() { Removal = "advanced", Summary = "演示用：可以清理" },
            ["com.demo.expert"] = new() { Removal = "expert", Summary = "专家级条目" },
            ["com.demo.unsafe"] = new() { Removal = "unsafe", Summary = "危险条目" },
            ["com.hicorenational.antifraud"] = new() { Removal = "recommended", Summary = "反诈中心" },
        });

        var catalogSnapshot = BuildCatalogSnapshot(device);
        var catalogPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = catalogSnapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = false },
            Catalog = testCatalog,
        });
        var catalogTargets = catalogPlan.Items
            .Where(i => i.Kind == PlanItemKind.Catalog)
            .ToDictionary(i => i.Target, i => i);

        Check("名册里 Recommended 的条目进了计划",
            catalogTargets.ContainsKey("com.demo.recommended"));
        Check("Recommended 且没有桌面图标 → 默认勾选", catalogTargets["com.demo.recommended"].IsSelected);
        Check("Recommended 但有桌面图标 → 不勾选（用户可能在用）",
            catalogTargets.ContainsKey("com.demo.recommended.visible")
            && !catalogTargets["com.demo.recommended.visible"].IsSelected);
        Check("Recommended 的风险标为低", catalogTargets["com.demo.recommended"].Risk == RiskLevel.Low);
        Check("Advanced 进计划但不勾选",
            catalogTargets.ContainsKey("com.demo.advanced") && !catalogTargets["com.demo.advanced"].IsSelected);
        Check("Expert 在一般档不进计划", !catalogTargets.ContainsKey("com.demo.expert"));
        Check("Unsafe 任何档位都不进计划", !catalogTargets.ContainsKey("com.demo.unsafe"));
        Check("名册不覆盖保护名单（反诈中心仍然受保护）",
            catalogPlan.Items.All(i => i.Target != "com.hicorenational.antifraud"));
        Check("名册条目的说明来自名册",
            catalogPlan.Items.First(i => i.Target == "com.demo.recommended").Reason == "演示用：推荐清理");

        var catalogGeekPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = catalogSnapshot,
            Rules = rules,
            Tier = OptimizationTier.Geek,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = false },
            Catalog = testCatalog,
        });
        Check("Expert 在极客档出现且不勾选",
            catalogGeekPlan.Items.Any(i => i.Target == "com.demo.expert" && !i.IsSelected));

        var realCatalog = AppCatalog.Load(appBaseDirectory);
        Check("随程序分发的离线名册可用", realCatalog.Count > 1000, $"实际 {realCatalog.Count} 条");
        text.AppendLine($"  名册：{realCatalog.Count} 条，来源 {realCatalog.GeneratedAt}");
        text.AppendLine($"  一般档名册条目 {catalogPlan.CatalogCount} 个（模拟设备）");

        // ---------- 4e. 建议（唯一的选择依据，没有白/黑名单） ----------
        text.AppendLine();
        text.AppendLine("[4e] 建议：列表展示与筛选共用的同一套口径");

        var adviceSamples = new (string Name, RuleRecord? Rule, ProtectionMatch Protection, CatalogEntry? Catalog, bool? Launcher, string Expect)[]
        {
            ("受保护 → 不要动", null, new ProtectionMatch(ProtectionLevel.Absolute, new ProtectionPattern { Match = "x" }),
                testCatalog.Find("com.demo.recommended"), false, Advice.TextProtectedStop),
            ("名册 recommended + 无图标 → 建议清理", null, ProtectionMatch.None,
                testCatalog.Find("com.demo.recommended"), false, Advice.TextClean),
            ("名册 recommended + 有图标 → 可清理", null, ProtectionMatch.None,
                testCatalog.Find("com.demo.recommended"), true, Advice.TextOptional),
            ("名册 advanced → 可清理", null, ProtectionMatch.None,
                testCatalog.Find("com.demo.advanced"), false, Advice.TextOptional),
            ("名册 unsafe → 不要动", null, ProtectionMatch.None,
                testCatalog.Find("com.demo.unsafe"), false, Advice.TextCatalogUnsafe),
            ("名册查不到 → 自行判断", null, ProtectionMatch.None, null, null, Advice.TextUnknown),
        };

        foreach (var (name, rule, protection, catalog, launcher, expect) in adviceSamples)
        {
            var (text2, _) = Advice.ForPackage(rule, protection, catalog, launcher);
            Check($"建议口径：{name}", text2 == expect, $"得到 {text2}，期望 {expect}");
        }

        Check("建议分组可用于筛选",
            Advice.ForPackage(null, ProtectionMatch.None, testCatalog.Find("com.demo.recommended"), false).Group == AdviceGroup.Clean
            && Advice.ForPackage(null, ProtectionMatch.None, testCatalog.Find("com.demo.recommended"), true).Group == AdviceGroup.Optional
            && Advice.ForPackage(null, ProtectionMatch.None, null, null).Group == AdviceGroup.Unknown);
        Check("筛选选项包含全部关键分组",
            Advice.FilterOptions.Contains("全部") && Advice.FilterOptions.Contains("建议清理")
            && Advice.FilterOptions.Contains("可清理") && Advice.FilterOptions.Contains("不要动")
            && Advice.FilterOptions.Contains("名单外"));
        Check("筛选匹配逻辑正确",
            Advice.MatchesFilter(AdviceGroup.Clean, "全部")
            && Advice.MatchesFilter(AdviceGroup.Clean, "建议清理")
            && !Advice.MatchesFilter(AdviceGroup.Clean, "不要动"));

        // AI 识别结果也要能体现在「建议」列上（用户填了 Key 之后点「识别未知应用」的场景）
        var aiHit = new PackageClassification
        {
            PackageName = "com.demo.ai", Category = "广告与跟踪",
            Action = PackageAction.Disable, Confidence = 0.9, Reason = "疑似推广",
        };
        Check("AI 有结论时建议显示 AI·建议处理",
            Advice.ForPackage(null, ProtectionMatch.None, null, null, aiHit).Text == Advice.TextAi);
        Check("AI 判为保留时不产生建议",
            Advice.ForPackage(null, ProtectionMatch.None, null, null,
                aiHit with { Action = PackageAction.Keep }).Text == Advice.TextUnknown);
        Check("名册资料优先于 AI",
            Advice.ForPackage(null, ProtectionMatch.None, testCatalog.Find("com.demo.recommended"), false, aiHit).Text
            == Advice.TextClean);

        // 计划条目和体检表必须用同一句话
        var planAdvice = Advice.ForPlanItem(catalogPlan.Items.First(i => i.Target == "com.demo.recommended"));
        Check("计划里的建议与体检表口径一致", planAdvice.Text == Advice.TextClean, planAdvice.Text);
        text.AppendLine($"  计划条目建议示例：{planAdvice.Text}（{Advice.GroupName(planAdvice.Group)}）");

        var geekPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Geek,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = true },
            AiSuggestions = aiSuggestions,
        });
        var geekTargets = geekPlan.Items.Select(i => i.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check("极客档包含一般档内容", geekTargets.Contains("com.xiaomi.market"));
        Check("极客档包含小爱同学", geekTargets.Contains("com.miui.voiceassist"));
        Check("极客档包含系统更新但默认不勾选",
            geekPlan.Items.Where(i => i.Target == "com.android.updater").All(i => !i.IsSelected));
        Check("低置信度条目被门槛过滤", !geekTargets.Contains("com.xiaomi.joyose"));
        Check("微信（guarded）默认被排除", !geekTargets.Contains("com.tencent.mm"));
        text.AppendLine($"  极客模式：{geekPlan.SelectionSummary}；跳过 {geekPlan.Skipped.Count} 项");

        var dangerPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Danger,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = true, AllowGuarded = true },
            AiSuggestions = aiSuggestions,
        });
        var dangerTargets = dangerPlan.Items.Select(i => i.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check("深度档纳入低置信度条目", dangerTargets.Contains("com.xiaomi.joyose"));
        Check("深度档默认不勾选低置信度条目",
            dangerPlan.Items.Where(i => i.Target == "com.xiaomi.joyose").All(i => !i.IsSelected));
        Check("解锁后微信进入候选", dangerTargets.Contains("com.tencent.mm"));
        Check("AI 仍不得动受保护组件",
            !dangerPlan.Items.Any(i => i.Kind == PlanItemKind.AiSuggestion && i.Target == "com.miui.guardprovider"));
        text.AppendLine();

        // ---------- 5. 幂等性 ----------
        text.AppendLine("[5] 重复执行保护（幂等性）");
        var afterApplied = BuildAppliedSnapshot(device);
        var secondPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = afterApplied,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = true },
        });
        var secondTargets = secondPlan.Items.Select(i => i.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check("已停用的应用不会再次执行", !secondTargets.Contains("com.miui.systemAdSolution"));
        Check("已卸载的应用不会再次执行", !secondTargets.Contains("com.android.browser"));
        text.AppendLine();

        // ---------- 6. 未知包筛选 ----------
        text.AppendLine("[6] 未知包筛选（AI 输入范围）");
        var unknown = builder.SelectUnknownTargets(snapshot, rules);
        Check("未知包里不含已收录应用", !unknown.Any(u => u.PackageName == "com.miui.market"));
        Check("未知包里不含受保护应用", !unknown.Any(u => u.PackageName == "com.miui.guardprovider"));
        Check("未知包里不含系统应用", unknown.All(u => !u.IsSystem));
        text.AppendLine($"  待识别：{unknown.Count} 个");
        text.AppendLine();

        // ---------- 7. 快照与报告 ----------
        text.AppendLine("[7] 快照与报告");
        var store = new SnapshotStore();
        var snapshotFile = new SnapshotFile
        {
            Id = "selftest",
            CreatedAt = DateTimeOffset.Now,
            DeviceModel = device.DisplayName,
            DeviceSerial = "SELFTEST",
            Brand = device.Brand,
            RomName = device.RomName,
            Tier = "Normal",
            ListVersion = rules.ListVersion,
            Entries =
            [
                new SnapshotEntry
                {
                    Kind = PlanItemKind.Package, Target = "com.demo", DisplayName = "演示",
                    ActionApplied = PackageAction.Disable, PresenceBefore = PackagePresence.Installed,
                },
            ],
        };
        var savedPath = store.Save(snapshotFile, Path.Combine(Path.GetTempPath(), $"aopt-selftest-{Guid.NewGuid():N}.json"));
        var loaded = store.Load(savedPath);
        Check("快照写入后可回读", loaded.Entries.Count == 1 && loaded.Entries[0].Target == "com.demo");
        File.Delete(savedPath);

        // 按序列号分目录之后，「回滚」页只能看到当前这台手机的快照——
        // 把 A 手机的快照还原到 B 手机上只会把 B 搞乱。
        var snapshotsRoot = Path.Combine(Path.GetTempPath(), $"aopt-snapshots-{Guid.NewGuid():N}");
        try
        {
            // 同一个目录里放着两台手机的快照（模拟旧版本留下的全局目录）
            var shared = new SnapshotStore(snapshotsRoot);
            shared.Save(snapshotFile with { Id = "aaa", DeviceSerial = "AAA" }, Path.Combine(snapshotsRoot, "aaa.json"));
            shared.Save(snapshotFile with { Id = "bbb", DeviceSerial = "BBB" }, Path.Combine(snapshotsRoot, "bbb.json"));

            var storeForA = new SnapshotStore(snapshotsRoot, serial: "AAA");
            var visible = storeForA.List();
            Check("快照列表只列这台手机自己的",
                visible.Count == 1 && visible[0].Snapshot.DeviceSerial == "AAA",
                string.Join("、", visible.Select(s => s.Snapshot.DeviceSerial)));
            Check("别的手机的快照不会出现在列表里（避免还原错手机）",
                visible.All(s => s.Snapshot.DeviceSerial != "BBB"));
        }
        finally
        {
            TryDeleteDirectory(snapshotsRoot);
        }

        var scanReport = ReportWriter.WriteScanReport(snapshot, rules, Path.Combine(Path.GetTempPath(), $"aopt-scan-{Guid.NewGuid():N}.md"));
        Check("体检报告可生成", File.Exists(scanReport) && new FileInfo(scanReport).Length > 200);
        File.Delete(scanReport);
        text.AppendLine();

        // ---------- 8. 密钥保护 ----------
        text.AppendLine("[8] 配置与密钥");
        var settings = new AppSettings { EnableAi = false, SafetyMode = true };
        settings.ApiKey = "sk-selftest-1234567890";
        Check("API Key 不以明文保存", settings.ApiKeyProtected is not null && !settings.ApiKeyProtected.Contains("sk-selftest"),
            settings.ApiKeyProtected ?? "(null)");
        Check("API Key 可正确解密", settings.ApiKey == "sk-selftest-1234567890", settings.ApiKey ?? "(null)");
        text.AppendLine();

        // ---------- 9. 端到端（模拟设备） ----------
        text.AppendLine("[9] 端到端演练（模拟安卓设备，不接触真机）");
        try
        {
            RunEndToEnd(rules, text, Check);
        }
        catch (Exception ex)
        {
            failures.Add("端到端演练未完成：" + ex.Message);
            text.AppendLine($"  [失败] 端到端演练抛出异常：{ex}");
        }
        text.AppendLine();

        // ---------- 10. APK 应用名解析 ----------
        text.AppendLine("[10] 应用名解析（从 APK 里读应用显示名）");
        var sampleApk = FindSampleApk(appBaseDirectory);
        if (sampleApk is null)
        {
            text.AppendLine("  跳过：没有找到样例 APK（legacy\\apks\\*.apk）。");
        }
        else
        {
            try
            {
                var bytes = File.ReadAllBytes(sampleApk);
                var label = ApkLabelReader.ReadFromApk(bytes);
                Check("能从真实 APK 里读出应用名", !string.IsNullOrWhiteSpace(label), label ?? "(null)");
                text.AppendLine($"  样例 {Path.GetFileName(sampleApk)} → {label}");
            }
            catch (Exception ex)
            {
                Check("解析样例 APK 没有抛异常", false, ex.Message);
            }
        }
        text.AppendLine();

        // ---------- 11. 权限画像 ----------
        text.AppendLine("[11] 权限画像（按权限快速筛出可疑应用）");

        var permissionRisks = rules.RuleSet.PermissionRisks;
        Check("名单里定义了值得警惕的权限", permissionRisks.Count >= 10, $"实际 {permissionRisks.Count} 条");

        var riskyProfile = PermissionAdvice.Build(
            [
                "android.permission.INTERNET",
                "android.permission.SYSTEM_ALERT_WINDOW",
                "android.permission.REQUEST_INSTALL_PACKAGES",
            ],
            permissionRisks);
        Check("命中悬浮窗与安装应用",
            riskyProfile.Hits.Any(h => h.Name == "悬浮窗") && riskyProfile.Hits.Any(h => h.Name == "安装应用"));
        Check("命中高危权限时等级为高",
            riskyProfile.HasHighRisk && riskyProfile.Level == RiskLevel.High);
        Check("权限列显示命中的权限短名",
            riskyProfile.DisplayText.Contains("悬浮窗") && riskyProfile.DisplayText.Contains("安装应用"),
            riskyProfile.DisplayText);
        Check("高危应用的排序值最小（点一次表头就是高危在前）", riskyProfile.SortRank == 0, riskyProfile.SortRank.ToString());

        var cleanProfile = PermissionAdvice.Build(["android.permission.INTERNET"], permissionRisks);
        Check("只申请普通权限时算干净",
            cleanProfile.Known && !cleanProfile.HasHighRisk && cleanProfile.Level == RiskLevel.Low);
        Check("干净应用的权限列显示「无」", cleanProfile.DisplayText == "无", cleanProfile.DisplayText);
        Check("干净应用排在后面", cleanProfile.SortRank == 2, cleanProfile.SortRank.ToString());

        var unknownProfile = PermissionAdvice.Build(null, permissionRisks);
        Check("没读到权限时标成未知，而不是「没有风险」",
            !unknownProfile.Known && unknownProfile.DisplayText == "—", unknownProfile.DisplayText);

        Check("可以按具体权限筛选",
            PermissionAdvice.MatchesFilter(riskyProfile, "悬浮窗")
            && !PermissionAdvice.MatchesFilter(cleanProfile, "悬浮窗"));
        Check("可以按「含高危权限」筛选",
            PermissionAdvice.MatchesFilter(riskyProfile, PermissionAdvice.FilterHigh)
            && !PermissionAdvice.MatchesFilter(cleanProfile, PermissionAdvice.FilterHigh));
        Check("可以按「无高危权限」筛选",
            PermissionAdvice.MatchesFilter(cleanProfile, PermissionAdvice.FilterLow)
            && !PermissionAdvice.MatchesFilter(riskyProfile, PermissionAdvice.FilterLow));
        Check("「未读取」不会被算进「无高危权限」",
            !PermissionAdvice.MatchesFilter(unknownProfile, PermissionAdvice.FilterLow)
            && PermissionAdvice.MatchesFilter(unknownProfile, PermissionAdvice.FilterUnknown));

        var permissionOptions = PermissionAdvice.FilterOptions(permissionRisks);
        Check("筛选下拉包含通用项与名单里的具体权限",
            permissionOptions.Contains(PermissionAdvice.FilterAll)
            && permissionOptions.Contains(PermissionAdvice.FilterHigh)
            && permissionOptions.Contains(PermissionAdvice.FilterLow)
            && permissionOptions.Contains("悬浮窗"),
            string.Join("、", permissionOptions));

        // 单条权限命中太散（真机上「读取所有应用」能命中三分之一的应用），组合才是能直接下手的信号
        var combos = rules.RuleSet.PermissionCombos;
        Check("名单里定义了权限组合", combos.Count >= 1, $"实际 {combos.Count} 条");
        Check("组合名不与具体权限名重复",
            combos.All(c => permissionRisks.All(r => !string.Equals(r.Name, c.Name, StringComparison.Ordinal))),
            string.Join("、", combos.Select(c => c.Name)));

        var comboProfile = PermissionAdvice.Build(
            [
                "android.permission.INTERNET",
                "android.permission.SYSTEM_ALERT_WINDOW",
                "android.permission.REQUEST_INSTALL_PACKAGES",
            ], permissionRisks, combos);
        Check("同时命中组合里的每一条才算命中组合",
            comboProfile.ComboHits.Any(c => c.Name == combos[0].Name),
            string.Join("、", comboProfile.ComboHits.Select(c => c.Name)));
        Check("只命中组合里的一条不算命中",
            PermissionAdvice.Build(["android.permission.SYSTEM_ALERT_WINDOW"], permissionRisks, combos)
                .ComboHits.Count == 0);
        Check("可以按组合名筛选",
            PermissionAdvice.MatchesFilter(comboProfile, combos[0].Name)
            && !PermissionAdvice.MatchesFilter(cleanProfile, combos[0].Name));
        Check("筛选下拉里有组合项（排在具体权限前面）",
            PermissionAdvice.FilterOptions(permissionRisks, combos).Contains(combos[0].Name));
        Check("组合会写进权限列的悬停说明",
            comboProfile.Summary.Contains("危险组合", StringComparison.Ordinal)
            && comboProfile.Summary.Contains(combos[0].Name, StringComparison.Ordinal));

        // dumpsys 的两种排版都要认：Android 13+ 带「: granted=true」，老版本只有权限名
        var parsedModern = DeviceScanner.ParseRequestedPermissions(
            "  requested permissions:\n" +
            "    android.permission.INTERNET: granted=true\n" +
            "    android.permission.SYSTEM_ALERT_WINDOW: granted=true\n" +
            "  install permissions:\n" +
            "    android.permission.INTERNET: granted=true\n");
        Check("能解析 Android 13+ 的权限段（带 granted 后缀）",
            parsedModern is { Count: 2 } && parsedModern[1] == "android.permission.SYSTEM_ALERT_WINDOW",
            parsedModern is null ? "(null)" : string.Join(",", parsedModern));

        var parsedLegacy = DeviceScanner.ParseRequestedPermissions(
            "  versionName=1.0\n" +
            "  requested permissions:\n" +
            "    android.permission.READ_SMS\n" +
            "    com.vendor.permission.CUSTOM\n" +
            "  declared permissions:\n" +
            "    com.vendor.permission.CUSTOM: prot=normal\n");
        Check("能解析老版本 dumpsys 的纯权限名列表",
            parsedLegacy is { Count: 2 } && parsedLegacy[0] == "android.permission.READ_SMS",
            parsedLegacy is null ? "(null)" : string.Join(",", parsedLegacy));
        Check("段尾的「declared permissions」不会被算成权限",
            parsedLegacy is { Count: 2 }, parsedLegacy is null ? "(null)" : parsedLegacy.Count.ToString());
        Check("输出里没有权限段时返回「未读取」",
            DeviceScanner.ParseRequestedPermissions("  versionName=1.0\n  codePath=/data/app/x/base.apk\n") is null);

        // 真机实测（HyperOS / Android 16）的排版：段头缩进 4 格、条目缩进 6 格，
        // 中间还夹着不像权限名的行，以及厂商拼错的「ACCESS_WIFI_ STATE」。
        // 早期版本遇到带空格的条目就认为「这一段结束了」，结果只读到前几条——所以这几种都要覆盖。
        var parsedHyperOs = DeviceScanner.ParseRequestedPermissions(
            "    requested permissions:\n" +
            "      com.sonyericsson.home.permission.BROADCAST_BADGE\n" +
            "      android.permission.SYSTEM_ALERT_WINDOW\n" +
            "      android.permission.ACCESS_WIFI_ STATE\n" +
            "      MediaStore.Images.Media.EXTERNAL_CONTENT_URI\n" +
            "      android.permission.REQUEST_INSTALL_PACKAGES\n" +
            "    install permissions:\n" +
            "      android.permission.INTERNET: granted=true\n");
        Check("能解析真机（HyperOS）的四空格缩进排版且不会中途停掉",
            parsedHyperOs is { Count: 5 }, parsedHyperOs is null
                ? "(null)"
                : $"{parsedHyperOs.Count} 条：{string.Join(",", parsedHyperOs)}");
        Check("真机排版里拼错的权限名也照读不误",
            parsedHyperOs?.Any(p => p.Contains("ACCESS_WIFI_", StringComparison.Ordinal)) == true);
        Check("真机排版能算出高危画像",
            PermissionAdvice.Build(parsedHyperOs, permissionRisks).HasHighRisk);

        // 扫描时顺带读权限：用一台模拟设备跑完整扫描 + 计划
        var permissionAdb = new SimulatedAdbClient("SIMPERM",
        [
            new SimulatedAdbClient.SimulatedPackage("com.demo.risky", System: false, VersionName: "1.0",
                Installer: "com.xiaomi.market", FirstInstall: "2026-01-01 00:00:00",
                Permissions: ["android.permission.SYSTEM_ALERT_WINDOW", "android.permission.REQUEST_INSTALL_PACKAGES"]),
        ]);
        var permissionScanner = new DeviceScanner(permissionAdb);
        var permissionDevice = permissionScanner.GetDeviceInfoAsync().GetAwaiter().GetResult();
        var permissionSnapshot = permissionScanner.ScanPackagesAsync(permissionDevice, deepScan: true).GetAwaiter().GetResult();
        var scannedPermissions = permissionSnapshot.Find("com.demo.risky")?.Permissions;
        Check("深度扫描顺带读到了应用的权限列表",
            scannedPermissions is { Count: 2 }, scannedPermissions is null ? "(null)" : string.Join(",", scannedPermissions));

        var scannedProfile = PermissionAdvice.Build(scannedPermissions, permissionRisks);
        Check("扫描结果能算出高危画像",
            scannedProfile.HasHighRisk && scannedProfile.DisplayText.Contains("悬浮窗"), scannedProfile.DisplayText);

        var permissionPlan = builder.Build(new PlanRequest
        {
            Device = permissionDevice,
            Snapshot = permissionSnapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = false },
        });
        var permissionItem = permissionPlan.Items.FirstOrDefault(i => i.Target == "com.demo.risky");
        Check("计划条目带上了权限画像（计划页也能筛）",
            permissionItem?.PermissionProfile.HasHighRisk == true,
            permissionItem?.PermissionProfile.DisplayText ?? "(没有这一项)");
        Check("权限画像不参与自动勾选（高危也不自动勾）",
            permissionItem is not null && !permissionItem.IsSelected);
        text.AppendLine($"  示例：com.demo.risky → {scannedProfile.DisplayText}（{permissionItem?.PermissionProfile.Tooltip.Split('\n')[1]}）");
        text.AppendLine();

        text.AppendLine("[12] 应用名缓存");
        var cachePath = Path.Combine(Path.GetTempPath(), $"aopt-labels-{Guid.NewGuid():N}.json");
        var cache = AppLabelCache.Load(cachePath);
        cache.Set("com.demo.app", "1.0.0", "演示应用");
        cache.SetUnreadable("com.demo.overlay", "1.0.0");
        cache.Save();
        var reloaded = AppLabelCache.Load(cachePath);
        Check("应用名缓存可保存并读回", reloaded.Get("com.demo.app", "1.0.0") == "演示应用",
            reloaded.Get("com.demo.app", "1.0.0") ?? "(null)");
        Check("版本变化后不会命中旧名字", reloaded.Get("com.demo.app", "2.0.0") is null);
        Check("「读不出来」也会记住，下次不再重试",
            reloaded.IsKnownUnreadable("com.demo.overlay", "1.0.0")
            && !reloaded.IsKnownUnreadable("com.demo.overlay", "2.0.0")
            && reloaded.Get("com.demo.overlay", "1.0.0") is null);
        TryDelete(cachePath);

        // 真机实测：20 个第三方应用里有 17 个的 ZIP 中央目录**不在**末尾 128 KB 的尾块里
        // （条目数过万，中央目录 1~3 MB）。早期版本硬要求中央目录落在尾块内，
        // 结果这些应用都读不出名字，而且失败不进缓存，每次扫描都重读一遍。
        var syntheticZip = BuildZipWithLargeCentralDirectory();
        var tailLength = Math.Min(syntheticZip.Length, 128 * 1024);
        var syntheticTailStart = syntheticZip.Length - tailLength;
        var syntheticTail = syntheticZip.AsSpan((int)syntheticTailStart).ToArray();
        var located = ZipCentralDirectory.Locate(syntheticTail, syntheticTailStart, syntheticZip.Length);
        Check("能从文件尾块定位到 ZIP 中央目录", located is not null,
            located is null ? "(没找到 EOCD)" : $"offset={located.Offset} size={located.Size}");
        Check("大应用的中央目录确实不在尾块里（这条是回归条件）",
            located is not null && located.Offset < syntheticTailStart,
            located is null ? "(null)" : $"中央目录 {located.Size / 1024} KB");
        if (located is not null)
        {
            var catalogBytes = syntheticZip.AsSpan((int)located.Offset, located.Size).ToArray();
            var locatedEntries = ZipCentralDirectory.Parse(catalogBytes, located.Offset, located);
            Check("单独读回中央目录后能解析出条目",
                locatedEntries.Any(e => e.Name == "AndroidManifest.xml"),
                $"解析出 {locatedEntries.Count} 条");

            var manifestEntry = locatedEntries.FirstOrDefault(e => e.Name == "AndroidManifest.xml");
            var extracted = manifestEntry is null
                ? null
                : ZipCentralDirectory.Extract(
                    syntheticZip.AsSpan((int)manifestEntry.LocalHeaderOffset).ToArray(), manifestEntry);
            Check("能按中央目录给的偏移从文件里取出条目内容",
                extracted is not null && Encoding.UTF8.GetString(extracted) == "FAKE-MANIFEST",
                extracted is null ? "(null)" : $"得到 {extracted.Length} 字节");
        }
        text.AppendLine();

        // ---------- 13. 多台手机的数据归档 ----------
        text.AppendLine("[13] 多台手机的数据归档与统计");
        var archiveRoot = Path.Combine(Path.GetTempPath(), $"aopt-devices-{Guid.NewGuid():N}");
        try
        {
            var archiveScan = permissionSnapshot;
            var archiveDevice = permissionDevice;

            var scanPath = DeviceArchive.SaveScan(archiveDevice, archiveScan, rules, catalog: null, devicesRoot: archiveRoot);
            Check("扫描结果按序列号归档", File.Exists(scanPath), scanPath);

            var profile = DeviceArchive.LoadProfile(archiveDevice.Serial, archiveRoot);
            Check("设备档案记下了型号与系统版本",
                profile is not null && profile.Model == archiveDevice.Model
                                    && profile.RomName == archiveDevice.RomName
                                    && profile.ScanCount == 1,
                profile?.TableSummary ?? "(null)");

            // 给同一台手机再扫一次：次数累加，但「首次见到」的时间不能被覆盖
            DeviceArchive.SaveScan(archiveDevice, archiveScan, rules, catalog: null, devicesRoot: archiveRoot);
            var profileAgain = DeviceArchive.LoadProfile(archiveDevice.Serial, archiveRoot);
            Check("重复扫描累加次数并保留首次见到时间",
                profileAgain is { ScanCount: 2 } && profile is not null && profileAgain.FirstSeenAt == profile.FirstSeenAt);

            var record = DeviceArchive.LoadLatestScan(archiveDevice.Serial, archiveRoot);
            Check("扫描记录里带上了应用名、权限与建议",
                record is not null && record.Packages.Any(p => p.Permissions is { Count: > 0 } && p.Advice is not null),
                $"{record?.Packages.Count ?? 0} 个包");
            Check("扫描记录里记下了这台手机的机型",
                record?.Model == archiveDevice.Model && record.Serial == archiveDevice.Serial);

        var archiveStats = DeviceArchive.BuildStatistics(appBaseDirectory, archiveRoot);
            Check("统计报告包含机型表与名单外应用",
                archiveStats.Contains("机型与系统版本", StringComparison.Ordinal)
                && archiveStats.Contains("名单外的第三方应用", StringComparison.Ordinal)
                && archiveStats.Contains("com.demo.risky", StringComparison.Ordinal));
            Check("统计报告不泄露序列号",
                !archiveStats.Contains(archiveDevice.Serial, StringComparison.OrdinalIgnoreCase),
                archiveDevice.Serial);
            Check("统计报告里没有权限数据时会说明（而不是编一个）",
                archiveStats.Contains("权限", StringComparison.Ordinal));

            text.AppendLine($"  归档目录：{AppPaths.Sanitize(archiveDevice.Serial)}（设备 {DeviceArchive.ListDevices(archiveRoot).Count} 台，" +
                            $"统计 {archiveStats.Length} 字符）");
        }
        finally
        {
            TryDeleteDirectory(archiveRoot);
        }
        text.AppendLine();

        // ---------- 14. 扫描缓存与「停用 / 卸载」手选 ----------
        text.AppendLine("[14] 扫描缓存（权限/安装来源）与手选动作");

        var detailCachePath = Path.Combine(Path.GetTempPath(), $"aopt-detail-{Guid.NewGuid():N}.json");
        try
        {
            var cacheAdb = new SimulatedAdbClient("SIMCACHE",
            [
                new SimulatedAdbClient.SimulatedPackage("com.demo.cached", System: false, VersionName: "1.0",
                    Installer: "com.xiaomi.market",
                    Permissions: ["android.permission.SYSTEM_ALERT_WINDOW"]),
            ]);
            var cacheScanner = new DeviceScanner(cacheAdb, detailCachePath: detailCachePath);
            var cacheDevice = cacheScanner.GetDeviceInfoAsync().GetAwaiter().GetResult();

            var firstScan = cacheScanner.ScanPackagesAsync(cacheDevice, deepScan: true).GetAwaiter().GetResult();
            var dumpsAfterFirst = cacheAdb.ExecutedCommands.Count(c => c.Contains("dumpsys package", StringComparison.Ordinal));
            Check("第一次扫描会真的去读权限与安装来源", dumpsAfterFirst > 0, $"dumpsys {dumpsAfterFirst} 次");
            Check("第一次扫描读到了权限", firstScan.Find("com.demo.cached")?.Permissions is { Count: > 0 });

            var secondScan = cacheScanner.ScanPackagesAsync(cacheDevice, deepScan: true).GetAwaiter().GetResult();
            var dumpsAfterSecond = cacheAdb.ExecutedCommands.Count(c => c.Contains("dumpsys package", StringComparison.Ordinal));
            Check("第二次扫描直接命中缓存，不再跑 dumpsys", dumpsAfterSecond == dumpsAfterFirst,
                $"累计 {dumpsAfterSecond} 次");
            Check("命中缓存照样能拿到权限与安装来源",
                secondScan.Find("com.demo.cached") is { Permissions.Count: > 0, Installer: "com.xiaomi.market" });

            var forcedScan = cacheScanner
                .ScanPackagesAsync(cacheDevice, deepScan: true, forceRefreshDetails: true)
                .GetAwaiter().GetResult();
            var dumpsAfterForce = cacheAdb.ExecutedCommands.Count(c => c.Contains("dumpsys package", StringComparison.Ordinal));
            Check("勾了「强制重新读取」就真的重新跑 dumpsys", dumpsAfterForce > dumpsAfterSecond,
                $"累计 {dumpsAfterForce} 次");
            Check("强制读取的结果一样可用", forcedScan.Find("com.demo.cached")?.Permissions is { Count: > 0 });
        }
        finally
        {
            TryDelete(detailCachePath);
        }

        // 「优化计划」页直接选停用/卸载：安全模式把卸载降级成停用时，
        // 用户明确改选「卸载」应当按用户选的执行（这是他在看清后果之后点的）。
        var overrideAdb = new SimulatedAdbClient("SIMOVR",
        [
            new SimulatedAdbClient.SimulatedPackage("com.miui.systemAdSolution", System: true),
            new SimulatedAdbClient.SimulatedPackage("com.miui.hybrid", System: true),
        ]);
        var overrideScanner = new DeviceScanner(overrideAdb);
        var overrideDevice = overrideScanner.GetDeviceInfoAsync().GetAwaiter().GetResult();
        var overrideSnapshot = overrideScanner.ScanPackagesAsync(overrideDevice, deepScan: false).GetAwaiter().GetResult();
        var overridePlan = builder.Build(new PlanRequest
        {
            Device = overrideDevice,
            Snapshot = overrideSnapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = true, IncludeSettings = false, IncludeUnknownPackages = false },
        });

        var adItem = overridePlan.Items.First(i => i.Target == "com.miui.systemAdSolution");
        Check("默认动作是「停用」（安全模式把卸载降级）",
            adItem.Action == PackageAction.Disable && adItem.ActionText == "停用", adItem.ActionText);
        Check("这一行允许用户在停用/卸载之间改", adItem.CanChooseAction);
        Check("后台限制这类条目不给改动作",
            overridePlan.Items.Where(i => i.Kind == PlanItemKind.Policy).All(i => !i.CanChooseAction));

        adItem.ActionOverride = PackageAction.Uninstall;
        Check("改成卸载后动作文案跟着变", adItem.EffectiveAction == PackageAction.Uninstall && adItem.ActionText == "卸载");
        Check("改过之后能看出是手动指定的", adItem.HasActionOverride);

        foreach (var item in overridePlan.Items) item.IsSelected = item == adItem;
        Check("汇总按用户选的动作统计",
            overridePlan.SelectionSummary.Contains("卸载 1 项", StringComparison.Ordinal), overridePlan.SelectionSummary);

        var overrideSnapshotDir = Path.Combine(Path.GetTempPath(), $"aopt-ovr-{Guid.NewGuid():N}");
        var overrideLogPath = Path.Combine(Path.GetTempPath(), $"aopt-ovr-{Guid.NewGuid():N}.log");
        try
        {
            using var overrideLog = new RunLogger(overrideLogPath);
            var overrideReport = new OptimizationExecutor(overrideAdb, new SnapshotStore(overrideSnapshotDir), overrideLog)
                .ExecuteAsync(overridePlan).GetAwaiter().GetResult();
            Check("执行时按用户选的动作走（系统应用也真的卸载了）",
                overrideReport.Results.Any(r => r.Item.Target == "com.miui.systemAdSolution")
                && !overrideAdb.IsInstalled("com.miui.systemAdSolution"),
                string.Join("；", overrideReport.Results.Select(r => $"{r.Item.Target}:{r.Status}")));
            Check("没勾选的应用没有被碰",
                overrideAdb.IsInstalled("com.miui.hybrid") && !overrideAdb.IsDisabled("com.miui.hybrid"));
        }
        finally
        {
            TryDeleteDirectory(overrideSnapshotDir);
            TryDelete(overrideLogPath);
        }
        text.AppendLine();

        // ---------- 15. 卸载前备份安装包（卸载了还能装回来）----------
        text.AppendLine("[15] 卸载前备份安装包，回滚用备份装回");
        var backupRoot = Path.Combine(Path.GetTempPath(), $"aopt-backup-{Guid.NewGuid():N}");
        var backupSnapshotDir = Path.Combine(Path.GetTempPath(), $"aopt-bk-snap-{Guid.NewGuid():N}");
        var backupLogPath = Path.Combine(Path.GetTempPath(), $"aopt-bk-{Guid.NewGuid():N}.log");
        try
        {
            var backupAdb = new SimulatedAdbClient("SIMBAK",
            [
                new SimulatedAdbClient.SimulatedPackage("com.demo.thirdparty", System: false,
                    Installer: "com.xiaomi.market"),
            ]);
            var backupScanner = new DeviceScanner(backupAdb);
            var backupDevice = backupScanner.GetDeviceInfoAsync().GetAwaiter().GetResult();
            var backupPlan = new PlanBuilder().Build(new PlanRequest
            {
                Device = backupDevice,
                Snapshot = backupScanner.ScanPackagesAsync(backupDevice, deepScan: false).GetAwaiter().GetResult(),
                Rules = rules,
                Tier = OptimizationTier.Normal,
                Options = new PlanOptions
                {
                    SafetyMode = false,
                    IncludeSettings = false,
                    IncludeUnknownPackages = true,
                    UnknownPackageAction = PackageAction.Uninstall,
                },
            });

            var target = backupPlan.Items.First(i => i.Target == "com.demo.thirdparty");
            foreach (var item in backupPlan.Items) item.IsSelected = item == target;
            Check("测试对象是「商店安装的第三方应用」", !target.IsSystem && target.EffectiveAction == PackageAction.Uninstall);

            using var backupLog = new RunLogger(backupLogPath);
            var backupStore = new SnapshotStore(backupSnapshotDir);
            var backupExecutor = new OptimizationExecutor(backupAdb, backupStore, backupLog)
            {
                BackupApksBeforeUninstall = true,
                DevicesRootOverride = backupRoot,
            };

            var backupReport = backupExecutor.ExecuteAsync(backupPlan).GetAwaiter().GetResult();
            Check("卸载执行成功", backupReport.FailedCount == 0 && !backupAdb.IsInstalled("com.demo.thirdparty"),
                string.Join("；", backupReport.Results.Select(r => $"{r.Item.Target}:{r.Status}")));

            var backupFiles = ApkBackupStore.Find("SIMBAK", "com.demo.thirdparty", backupRoot);
            Check("卸载前把安装包备份到了电脑上", backupFiles.Count > 0,
                string.Join("、", backupFiles.Select(Path.GetFileName)));

            var backupSnapshotPath = backupReport.SnapshotPath!;
            var backupSnapshot = backupStore.Load(backupSnapshotPath);
            var backupEntry = backupSnapshot.Entries.First(e => e.Target == "com.demo.thirdparty");
            Check("快照里记下了备份位置", !string.IsNullOrWhiteSpace(backupEntry.ApkBackupDirectory),
                backupEntry.ApkBackupDirectory ?? "(null)");

            // 用这份快照回滚：应当用电脑上的备份装回来
            var rollback = new RollbackService(backupAdb, backupLog)
                .RestoreAsync(backupSnapshot, backupSnapshotPath, devicesRootOverride: backupRoot)
                .GetAwaiter().GetResult();
            Check("回滚没有失败项", rollback.FailedCount == 0,
                string.Join("；", rollback.Results.Where(r => !r.Success).Select(r => $"{r.Target}:{r.Message}")));
            Check("被卸载的第三方应用真的装回来了", backupAdb.IsInstalled("com.demo.thirdparty"));
            text.AppendLine($"  备份与回滚：{string.Join("；", rollback.Results.Select(r => r.Message))}");

            // 关掉备份：这类应用卸载后是装不回来的，所以根本不该卸
            var noBackupAdb = new SimulatedAdbClient("SIMNOBAK",
            [
                new SimulatedAdbClient.SimulatedPackage("com.demo.nobackup", System: false),
            ]);
            var noBackupScanner = new DeviceScanner(noBackupAdb);
            var noBackupDevice = noBackupScanner.GetDeviceInfoAsync().GetAwaiter().GetResult();
            var noBackupPlan = new PlanBuilder().Build(new PlanRequest
            {
                Device = noBackupDevice,
                Snapshot = noBackupScanner.ScanPackagesAsync(noBackupDevice, deepScan: false).GetAwaiter().GetResult(),
                Rules = rules,
                Tier = OptimizationTier.Normal,
                Options = new PlanOptions
                {
                    SafetyMode = false,
                    IncludeSettings = false,
                    IncludeUnknownPackages = true,
                    UnknownPackageAction = PackageAction.Uninstall,
                },
            });
            foreach (var item in noBackupPlan.Items) item.IsSelected = item.Target == "com.demo.nobackup";

            var noBackupReport = new OptimizationExecutor(noBackupAdb, new SnapshotStore(backupSnapshotDir), backupLog)
            {
                BackupApksBeforeUninstall = false,   // 关掉备份：模拟「以前那种危险配置」
                DevicesRootOverride = backupRoot,
            }.ExecuteAsync(noBackupPlan).GetAwaiter().GetResult();
            Check("关掉备份时仍然会卸载（这是危险的，界面上有明确警告）",
                noBackupReport.Results.Any(r => r.Item.Target == "com.demo.nobackup")
                && !noBackupAdb.IsInstalled("com.demo.nobackup"));
        }
        finally
        {
            TryDeleteDirectory(backupRoot);
            TryDeleteDirectory(backupSnapshotDir);
            TryDelete(backupLogPath);
        }
        text.AppendLine();

        text.AppendLine("========== 结果 ==========");
        text.AppendLine($"检查项：{checks}，失败：{failures.Count}");
        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                text.AppendLine($"  失败：{failure}");
            }
        }
        text.AppendLine(failures.Count == 0 ? "全部通过。" : "存在失败项，请检查名单或计划逻辑。");

        return text.ToString();
    }

    /// <summary>
    /// 用模拟设备跑完整流程：扫描 → 生成计划 → 执行 → 回读校验 → 回滚 → 再校验。
    /// 这一段覆盖的是真正会改动手机的那部分代码。
    /// </summary>
    private static void RunEndToEnd(RuleRepository rules, StringBuilder text, CheckReporter check)
    {
        var devicePackages = new (string Name, bool System, bool Disabled, bool? NeverLaunched)[]
        {
            ("com.miui.systemAdSolution", true, false, null),
            ("com.miui.analytics", true, false, null),
            ("com.miui.hybrid", true, false, null),
            ("com.xiaomi.market", true, false, null),
            ("com.android.browser", true, false, null),
            ("com.miui.guardprovider", true, false, null),
            ("com.android.systemui", true, false, null),
            ("com.hicorenational.antifraud", true, false, null),
            ("com.unknown.adapp", false, false, true),
            ("com.taobao.taobao", false, false, false),
            ("com.autonavi.minimap", false, false, true),
        };

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["global/window_animation_scale"] = "1.0",
            ["global/transition_animation_scale"] = "1.0",
            ["global/animator_duration_scale"] = "1.0",
        };

        var adb = new SimulatedAdbClient("SIM0001", devicePackages, settings);
        var scanner = new DeviceScanner(adb);
        var device = scanner.GetDeviceInfoAsync().GetAwaiter().GetResult();
        var snapshot = scanner.ScanPackagesAsync(device, deepScan: true).GetAwaiter().GetResult();

        check("模拟设备识别出品牌", device.BrandTokens.Contains("xiaomi"), string.Join(",", device.BrandTokens));
        check("模拟设备系统识别为 HyperOS", device.RomName == "HyperOS", device.RomName);
        check("扫描到全部已安装应用", snapshot.Packages.Values.Count(p => p.Installed) == devicePackages.Length,
            $"{snapshot.Packages.Values.Count(p => p.Installed)}/{devicePackages.Length}");
        check("深度扫描读到了安装来源",
            snapshot.Find("com.unknown.adapp")?.Installer == "com.xiaomi.market",
            snapshot.Find("com.unknown.adapp")?.Installer ?? "(null)");
        check("深度扫描读到了「从未打开过」标志",
            snapshot.Find("com.unknown.adapp")?.NeverLaunched == true,
            snapshot.Find("com.unknown.adapp")?.NeverLaunched?.ToString() ?? "(null)");
        check("打开过的应用不会被误标成「从未打开过」",
            snapshot.Find("com.taobao.taobao")?.NeverLaunched == false,
            snapshot.Find("com.taobao.taobao")?.NeverLaunched?.ToString() ?? "(null)");

        // 关闭安全模式，让「卸载」这一步真正走卸载分支，从而连回滚一起验证。
        var plan = new PlanBuilder().Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions
            {
                SafetyMode = false,
                IncludeSettings = true,
                EnabledPolicies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sensor_lock" },
            },
        });

        check("计划里包含卸载动作", plan.Items.Any(i => i.Action == PackageAction.Uninstall));
        check("计划里包含传感器策略项", plan.Items.Any(i => i.Kind == PlanItemKind.Policy));

        var logPath = Path.Combine(Path.GetTempPath(), $"aopt-e2e-{Guid.NewGuid():N}.log");
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"aopt-e2e-{Guid.NewGuid():N}.json");
        using var logger = new RunLogger(logPath);
        var store = new SnapshotStore();
        var executor = new OptimizationExecutor(adb, store, logger);

        var report = executor.ExecuteAsync(plan).GetAwaiter().GetResult();
        store.Save(report.Snapshot!, snapshotPath);

        check("执行没有失败项", report.FailedCount == 0,
            string.Join("；", report.Results.Where(r => r.Status == ActionStatus.Failed).Select(r => $"{r.Item.Target}:{r.Message}")));
        check("每个成功项都通过了回读校验",
            report.Results.Where(r => r.Status is ActionStatus.Success or ActionStatus.Downgraded).All(r => r.Verified));
        check("设备上广告服务确实被停用", adb.IsDisabled("com.miui.systemAdSolution"));
        check("设备上快应用框架确实被停用", adb.IsDisabled("com.miui.hybrid"));
        check("设备上浏览器确实被卸载", !adb.IsInstalled("com.android.browser"));
        check("受保护的反诈应用未被触碰", adb.IsInstalled("com.hicorenational.antifraud") && !adb.IsDisabled("com.hicorenational.antifraud"));
        check("受保护的安全守护未被触碰", adb.IsInstalled("com.miui.guardprovider") && !adb.IsDisabled("com.miui.guardprovider"));
        check("动画缩放已改为 0.75", adb.GetSetting("global/window_animation_scale") == "0.75",
            adb.GetSetting("global/window_animation_scale") ?? "(null)");
        check("未知名应用被关掉了传感器权限",
            adb.GetAppOp("com.unknown.adapp", "BODY_SENSORS") == "ignore",
            adb.GetAppOp("com.unknown.adapp", "BODY_SENSORS") ?? "(未设置)");
        check("地图类应用没有被关传感器",
            adb.GetAppOp("com.autonavi.minimap", "BODY_SENSORS") is null,
            adb.GetAppOp("com.autonavi.minimap", "BODY_SENSORS") ?? "(未设置)");
        check("只关传感器时不改后台分桶",
            adb.GetStandbyBucket("com.unknown.adapp") is null,
            adb.GetStandbyBucket("com.unknown.adapp") ?? "(未设置)");
        check("执行后保存了可回滚快照", report.SnapshotPath is not null && File.Exists(report.SnapshotPath));
        text.AppendLine($"  执行结果：{report.Summary}，其中 {report.VerifiedCount} 项通过回读校验");

        // ---- 回滚 ----
        var loaded = store.Load(snapshotPath);
        var rollback = new RollbackService(adb, logger).RestoreAsync(loaded, snapshotPath).GetAwaiter().GetResult();

        check("回滚没有失败项", rollback.FailedCount == 0,
            string.Join("；", rollback.Results.Where(r => !r.Success).Select(r => $"{r.Target}:{r.Message}")));
        check("回滚后广告服务恢复启用", !adb.IsDisabled("com.miui.systemAdSolution"));
        check("回滚后快应用框架恢复启用", !adb.IsDisabled("com.miui.hybrid"));
        check("回滚后浏览器被重新装回", adb.IsInstalled("com.android.browser"));
        check("回滚后动画缩放恢复为 1.0", adb.GetSetting("global/window_animation_scale") == "1.0",
            adb.GetSetting("global/window_animation_scale") ?? "(null)");
        check("回滚后传感器权限恢复为默认",
            adb.GetAppOp("com.unknown.adapp", "BODY_SENSORS") == "default",
            adb.GetAppOp("com.unknown.adapp", "BODY_SENSORS") ?? "(未设置)");
        text.AppendLine($"  回滚结果：成功 {rollback.SuccessCount} 项，失败 {rollback.FailedCount} 项");

        // ---- 重复执行的幂等性（基于真机状态回读） ----
        var afterRollback = scanner.ScanPackagesAsync(device, deepScan: false).GetAwaiter().GetResult();
        var secondPlan = new PlanBuilder().Build(new PlanRequest
        {
            Device = device,
            Snapshot = afterRollback,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions { SafetyMode = false, IncludeSettings = true },
        });
        check("回滚后重新扫描，候选数量和第一次一致",
            secondPlan.Items.Count(i => i.Kind == PlanItemKind.Package) == plan.Items.Count(i => i.Kind == PlanItemKind.Package),
            $"{secondPlan.Items.Count(i => i.Kind == PlanItemKind.Package)} vs {plan.Items.Count(i => i.Kind == PlanItemKind.Package)}");

        // ---- 未知应用「一键清理」的完整演练 ----
        var beforeUnknown = scanner.ScanPackagesAsync(device, deepScan: true).GetAwaiter().GetResult();

        // 真机上名册总是带着的：地图这类应用在名册里有条目，就成了「名册条目」而不是「未知应用」，
        // 因此不会被「选中全部名单外应用」误伤。这里把这一层也演出来。
        var e2eCatalog = AppCatalog.FromEntries(new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["com.autonavi.minimap"] = new() { Removal = "recommended", Summary = "地图导航" },
        });

        var unknownPlan = new PlanBuilder().Build(new PlanRequest
        {
            Device = device,
            Snapshot = beforeUnknown,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Catalog = e2eCatalog,
            Options = new PlanOptions
            {
                SafetyMode = true,
                IncludeSettings = false,
                IncludeUnknownPackages = true,
            },
        });

        // 模拟用户点「一键选中」：只留未知应用
        foreach (var item in unknownPlan.Items) item.IsSelected = false;
        foreach (var item in unknownPlan.UnknownItems) item.IsSelected = true;
        check("一键选中后计划里只剩未知应用", unknownPlan.SelectedItems.All(i => i.Kind == PlanItemKind.Unknown));

        var unknownReport = executor.ExecuteAsync(unknownPlan).GetAwaiter().GetResult();
        check("未知应用一键清理没有失败项", unknownReport.FailedCount == 0,
            string.Join("；", unknownReport.Results.Where(r => r.Status == ActionStatus.Failed).Select(r => $"{r.Item.Target}:{r.Message}")));
        check("未知应用确实被停用", adb.IsDisabled("com.unknown.adapp"));
        check("从未打开过的应用被处理",
            unknownPlan.UnknownItems.Any(i => i.Target == "com.unknown.adapp" && i.NeverLaunched == true));
        check("名册收录的应用不会被「选中全部名单外」误伤",
            unknownPlan.Items.Any(i => i.Target == "com.autonavi.minimap" && i.Kind == PlanItemKind.Catalog)
            && !adb.IsDisabled("com.autonavi.minimap"));
        check("受保护应用没有被一键清理误伤", !adb.IsDisabled("com.tencent.mm"));

        var unknownSnapshot = store.Save(unknownReport.Snapshot!, Path.Combine(Path.GetTempPath(), $"aopt-unknown-{Guid.NewGuid():N}.json"));
        var unknownRollback = new RollbackService(adb, logger)
            .RestoreAsync(store.Load(unknownSnapshot), unknownSnapshot).GetAwaiter().GetResult();
        check("一键清理可以整体回滚", unknownRollback.FailedCount == 0 && !adb.IsDisabled("com.unknown.adapp"));
        TryDelete(unknownSnapshot);
        text.AppendLine($"  未知应用一键清理：处理 {unknownReport.Results.Count} 项，回滚成功 {unknownRollback.SuccessCount} 项");

        TryDelete(snapshotPath);
        TryDelete(logPath);
    }

    /// <summary>
    /// 造一个「中央目录大于 128 KB」的 zip，用来复现真机上的大应用：
    /// 尾块里只有 EOCD，条目的偏移与长度必须再单独读一次中央目录才能拿到。
    /// </summary>
    private static byte[] BuildZipWithLargeCentralDirectory()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(archive, "AndroidManifest.xml", "FAKE-MANIFEST");
            WriteZipEntry(archive, "resources.arsc", "FAKE-ARSC");
            for (var i = 0; i < 3000; i++) WriteZipEntry(archive, $"res/drawable/icon_{i}.png", "x");
        }
        return stream.ToArray();
    }

    private static void WriteZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败可以忽略。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录清理失败可以忽略。
        }
    }

    /// <summary>找一个真实 APK 用来验证解析器；仓库里没有就跳过（不阻塞自检）。</summary>
    private static string? FindSampleApk(string? appBaseDirectory)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(appBaseDirectory)) roots.Add(appBaseDirectory!);
        roots.Add(AppContext.BaseDirectory);

        foreach (var root in roots)
        {
            var dir = new DirectoryInfo(root);
            for (var depth = 0; depth < 7 && dir is not null; depth++, dir = dir.Parent)
            {
                var apkDir = Path.Combine(dir.FullName, "legacy", "apks");
                if (!Directory.Exists(apkDir)) continue;

                var apk = Directory.EnumerateFiles(apkDir, "*.apk").FirstOrDefault();
                if (apk is not null) return apk;
            }
        }

        return null;
    }

    /// <summary>只放几条「名册里有资料」的系统应用，用来验证名册驱动的计划生成。</summary>
    private static PackageSnapshot BuildCatalogSnapshot(DeviceInfo device)
    {
        string[] names =
        [
            "com.demo.recommended",
            "com.demo.recommended.visible",
            "com.demo.advanced",
            "com.demo.expert",
            "com.demo.unsafe",
            "com.hicorenational.antifraud",
        ];

        var map = names.ToDictionary(
            name => name,
            name => new PackageEntry
            {
                Name = name,
                Installed = true,
                IsSystem = true,
                IsThirdParty = false,
                Label = name,
                // 只有 .visible 这条模拟「桌面上有图标」
                HasLauncher = name.EndsWith(".visible", StringComparison.Ordinal),
            },
            StringComparer.OrdinalIgnoreCase);

        return new PackageSnapshot
        {
            Device = device,
            ScannedAt = DateTimeOffset.Now,
            Packages = map,
            SystemCount = map.Count,
            ThirdPartyCount = 0,
            DetailEnriched = false,
            Warnings = [],
        };
    }

    private static DeviceInfo BuildSyntheticDevice() => new()
    {
        Serial = "SELFTEST1234",
        Brand = "Redmi",
        Manufacturer = "Xiaomi",
        Model = "Redmi Note 12 Turbo",
        Device = "marble",
        AndroidRelease = "14",
        SdkInt = 34,
        BuildId = "UKQ1.231003.002",
        RomName = "HyperOS",
        RomVersion = "1.0.8.0",
        CpuAbi = "arm64-v8a",
        BrandTokens = DeviceScanner.BuildBrandTokens("Redmi", "Xiaomi"),
    };

    private static PackageSnapshot BuildSyntheticSnapshot(DeviceInfo device)
    {
        (string Name, bool System, string? Installer, bool? NeverLaunched)[] entries =
        [
            ("com.miui.systemAdSolution", true, null, null),
            ("com.miui.analytics", true, null, null),
            ("com.miui.hybrid", true, null, null),
            ("com.xiaomi.market", true, null, null),
            ("com.android.browser", true, null, null),
            ("com.android.updater", true, null, null),
            ("com.miui.voiceassist", true, null, null),
            ("com.xiaomi.joyose", true, null, null),
            ("com.miui.guardprovider", true, null, null),
            ("com.miui.securitycenter", true, null, null),
            ("com.baidu.input_mi", true, null, null),
            ("com.android.systemui", true, null, null),
            ("com.hicorenational.antifraud", true, null, null),
            ("com.tencent.mm", false, "com.xiaomi.market", false),
            ("com.unknown.adapp", false, "com.xiaomi.market", true),
            ("com.unknown.helper", false, "com.android.packageinstaller", false),
            ("com.ss.android.ugc.aweme", false, "com.xiaomi.market", false),
            ("com.taobao.taobao", false, "com.xiaomi.market", false),
            ("com.autonavi.minimap", false, "com.xiaomi.market", true),
            ("com.huawei.health", false, "com.xiaomi.market", true),
        ];

        var map = entries.ToDictionary(
            e => e.Name,
            e => new PackageEntry
            {
                Name = e.Name,
                Installed = true,
                IsSystem = e.System,
                IsThirdParty = !e.System,
                Installer = e.Installer,
                VersionName = "1.0",
                FirstInstallTime = DateTimeOffset.Now.AddDays(-30),
                NeverLaunched = e.NeverLaunched,
            },
            StringComparer.OrdinalIgnoreCase);

        return new PackageSnapshot
        {
            Device = device,
            ScannedAt = DateTimeOffset.Now,
            Packages = map,
            SystemCount = entries.Count(e => e.System),
            ThirdPartyCount = entries.Count(e => !e.System),
            DetailEnriched = true,
            Warnings = [],
        };
    }

    /// <summary>模拟「上次已经优化过」的状态，用于验证不会重复处理。</summary>
    private static PackageSnapshot BuildAppliedSnapshot(DeviceInfo device)
    {
        var baseline = BuildSyntheticSnapshot(device);
        var map = new Dictionary<string, PackageEntry>(baseline.Packages, StringComparer.OrdinalIgnoreCase);

        map["com.miui.systemAdSolution"] = map["com.miui.systemAdSolution"] with { Disabled = true };
        map["com.miui.hybrid"] = map["com.miui.hybrid"] with { Disabled = true };
        map["com.android.browser"] = map["com.android.browser"] with { Installed = false, RemovedForUser = true };

        return new PackageSnapshot
        {
            Device = device,
            ScannedAt = DateTimeOffset.Now,
            Packages = map,
            SystemCount = baseline.SystemCount,
            ThirdPartyCount = baseline.ThirdPartyCount,
            DetailEnriched = true,
            Warnings = [],
        };
    }
}
