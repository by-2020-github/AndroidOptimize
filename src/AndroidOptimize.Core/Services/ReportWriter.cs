using System.Text;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

public static class ReportWriter
{
    public static string WriteScanReport(PackageSnapshot snapshot, RuleRepository rules, string? path = null)
    {
        // 报告按手机分目录：给别人优化多台手机时，每台的报告都在自己的目录里。
        path ??= AppPaths.NewReportPath(snapshot.Device.DisplayName, serial: snapshot.Device.Serial);
        var text = new StringBuilder();

        text.AppendLine("# 手机体检报告");
        text.AppendLine();
        text.AppendLine($"> 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine();
        text.AppendLine("## 设备信息");
        text.AppendLine();
        text.AppendLine("| 项目 | 内容 |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| 设备 | {snapshot.Device.DisplayName} |");
        text.AppendLine($"| 品牌 | {snapshot.Device.Brand} |");
        text.AppendLine($"| 系统 | {snapshot.Device.SystemSummary} |");
        text.AppendLine($"| 型号代号 | {snapshot.Device.Device} |");
        text.AppendLine($"| 序列号 | {snapshot.Device.Serial} |");
        text.AppendLine($"| 名单版本 | {rules.ListVersion} |");
        text.AppendLine();
        text.AppendLine("## 应用统计");
        text.AppendLine();
        text.AppendLine($"- 已安装应用：{snapshot.Packages.Values.Count(p => p.Installed)} 个");
        text.AppendLine($"- 其中系统应用：{snapshot.SystemCount} 个");
        text.AppendLine($"- 其中第三方应用：{snapshot.ThirdPartyCount} 个");
        text.AppendLine($"- 名单已覆盖规则：{rules.PackageRuleCount} 条");
        text.AppendLine($"- 保护规则：{rules.ProtectionRuleCount} 条");
        text.AppendLine();

        var thirdParty = snapshot.Packages.Values
            .Where(p => p.Installed && p.IsThirdParty)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (thirdParty.Count > 0)
        {
            text.AppendLine("## 已安装的第三方应用");
            text.AppendLine();
            text.AppendLine("| 包名 | 版本 | 安装来源 | 首次安装 | 名单状态 |");
            text.AppendLine("|---|---|---|---|---|");
            foreach (var entry in thirdParty)
            {
                var rule = rules.FindPackageRule(entry.Name, snapshot.Device.BrandTokens);
                var protection = rules.MatchProtection(entry.Name);
                var state = protection.IsProtected ? $"受保护（{protection.Pattern.Category}）"
                    : rule is not null ? $"名单：{rule.DisplayName}"
                    : "未知";
                text.AppendLine($"| `{entry.Name}` | {entry.VersionName ?? "-"} | {entry.Installer ?? "-"} | " +
                                $"{(entry.FirstInstallTime is null ? "-" : entry.FirstInstallTime.Value.ToString("yyyy-MM-dd"))} | {state} |");
            }
            text.AppendLine();
        }

        if (snapshot.Warnings.Count > 0)
        {
            text.AppendLine("## 扫描提示");
            text.AppendLine();
            foreach (var warning in snapshot.Warnings)
            {
                text.AppendLine($"- {warning}");
            }
            text.AppendLine();
        }

        text.AppendLine("---");
        text.AppendLine();
        text.AppendLine("*本报告由 AndroidOptimize 生成，仅包含应用清单与状态，不包含任何个人数据。*");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        return path;
    }

    public static string WriteExecutionReport(ExecutionReport report, OptimizationPlan plan, string? path = null)
    {
        path ??= AppPaths.NewReportPath(report.Device.DisplayName, serial: report.Device.Serial);
        var text = new StringBuilder();

        text.AppendLine("# 优化执行报告");
        text.AppendLine();
        text.AppendLine($"> 执行时间：{report.StartedAt:yyyy-MM-dd HH:mm:ss} ~ {report.FinishedAt:HH:mm:ss}（耗时 {report.Duration.TotalSeconds:0.0} 秒）");
        text.AppendLine($"> 优化档位：{plan.Tier}");
        text.AppendLine($"> 结果：{report.Summary}");
        text.AppendLine();

        if (report.Notes.Count > 0)
        {
            foreach (var note in report.Notes)
            {
                text.AppendLine($"- {note}");
            }
            text.AppendLine();
        }

        text.AppendLine("## 执行明细");
        text.AppendLine();
        text.AppendLine("| # | 项目 | 包名/设置项 | 动作 | 结果 | 回读校验 | 说明 |");
        text.AppendLine("|---|---|---|---|---|---|---|");

        var index = 0;
        foreach (var result in report.Results)
        {
            index++;
            var verify = result.Status is ActionStatus.Failed or ActionStatus.Skipped
                ? "-"
                : result.Verified ? "通过" : "未确认";
            var message = result.Message.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            text.AppendLine($"| {index} | {result.Item.DisplayName} | `{result.Item.Target}` | {result.Item.ActionText} | " +
                            $"{result.StatusText} | {verify} | {message} |");
        }
        text.AppendLine();

        var failed = report.Results.Where(r => r.Status == ActionStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            text.AppendLine("## 失败项与建议");
            text.AppendLine();
            foreach (var result in failed)
            {
                text.AppendLine($"### {result.Item.DisplayName}（`{result.Item.Target}`）");
                text.AppendLine();
                text.AppendLine($"- 原因：{result.Message}");
                if (!string.IsNullOrWhiteSpace(result.Hint)) text.AppendLine($"- 建议：{result.Hint}");
                if (!string.IsNullOrWhiteSpace(result.RawOutput)) text.AppendLine($"- 原始输出：`{result.RawOutput.Replace("\n", " ").Replace("`", "'")}`");
                text.AppendLine();
            }
        }

        if (report.Snapshot is not null)
        {
            text.AppendLine("## 回滚方式");
            text.AppendLine();
            text.AppendLine($"本次执行的快照已保存：`{report.SnapshotPath}`");
            text.AppendLine();
            text.AppendLine("在程序里打开「回滚」页，选择本次快照点击还原，即可把手机恢复原状。");
            text.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(report.LogPath))
        {
            text.AppendLine($"完整日志：`{report.LogPath}`");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        return path;
    }
}
