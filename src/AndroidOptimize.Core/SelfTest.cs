using System.Text;
using AndroidOptimize.Core.Adb;
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
        Check("名单外的正常应用（地图 / 健康）不算未知",
            !unknownTargets.Contains("com.autonavi.minimap") && !unknownTargets.Contains("com.huawei.health"),
            string.Join(",", unknownTargets));
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

        // ---------- 4d. 懒人模式（白名单之外全部处理） ----------
        text.AppendLine();
        text.AppendLine("[4d] 懒人模式（不做猜测，只看白名单）");

        Check("白名单里有微信", rules.IsLazyModeKeep("com.tencent.mm"));
        Check("白名单里有抖音和快手",
            rules.IsLazyModeKeep("com.ss.android.ugc.aweme") && rules.IsLazyModeKeep("com.smile.gifmaker"));
        Check("白名单里有地图导航（通配符）",
            rules.IsLazyModeKeep("com.autonavi.minimap") && rules.IsLazyModeKeep("com.baidu.BaiduMap")
            && rules.IsLazyModeKeep("com.tencent.map"));
        Check("保护名单自动保留（银行）", rules.IsLazyModeKeep("com.icbc"));
        Check("保护名单自动保留（反诈）", rules.IsLazyModeKeep("com.hicorenational.antifraud"));
        Check("保护名单自动保留（输入法）", rules.IsLazyModeKeep("com.baidu.input_mi"));
        Check("白名单之外的推广类应用会被处理",
            !rules.IsLazyModeKeep("com.lucky.wifi") && !rules.IsLazyModeKeep("com.clean.master"));
        Check("白名单之外、程序也不认识的第三方应用会被处理", !rules.IsLazyModeKeep("com.unknown.adapp"));

        var lazyPlan = builder.Build(new PlanRequest
        {
            Device = device,
            Snapshot = snapshot,
            Rules = rules,
            Tier = OptimizationTier.Normal,
            Options = new PlanOptions
            {
                SafetyMode = true,
                IncludeSettings = true,
                IncludeUnknownPackages = true,
                EnabledPolicies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sensor_lock" },
            },
        });
        var lazyKept = PlanBuilder.ApplyLazyMode(lazyPlan, rules);

        Check("懒人模式把未知应用也勾上",
            lazyPlan.SelectedItems.Any(i => i.Kind == PlanItemKind.Unknown));
        Check("懒人模式保留白名单里的应用",
            lazyPlan.SelectedItems.All(i => !rules.IsLazyModeKeep(i.Target)));
        Check("懒人模式保留微信（即使它出现在计划里）",
            lazyPlan.SelectedItems.All(i => i.Target != "com.tencent.mm"));
        Check("懒人模式会把应用商店一起处理",
            lazyPlan.SelectedItems.Any(i => i.Target == "com.xiaomi.market"));
        Check("懒人模式动系统组件但不动保护项",
            lazyPlan.SelectedItems.All(i => !rules.MatchProtection(i.Target).IsProtected));
        Check("懒人模式保留设置项", lazyPlan.SelectedItems.Any(i => i.Kind == PlanItemKind.Setting));
        text.AppendLine($"  懒人模式：勾选 {lazyPlan.SelectedCount} 项，保留 {lazyKept.Count} 项");

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
        var unknownPlan = new PlanBuilder().Build(new PlanRequest
        {
            Device = device,
            Snapshot = beforeUnknown,
            Rules = rules,
            Tier = OptimizationTier.Normal,
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
        check("地图类应用没有被一键清理误伤", !adb.IsDisabled("com.autonavi.minimap"));
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
