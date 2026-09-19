using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

/// <summary>
/// 执行器：先落快照，再逐项执行，最后回读设备状态做校验。
/// 单项失败不会中断整个流程，失败项会带中文原因返回给界面。
/// </summary>
public sealed class OptimizationExecutor
{
    private readonly AdbClient _adb;
    private readonly SnapshotStore _snapshots;
    private readonly RunLogger _log;

    public OptimizationExecutor(AdbClient adb, SnapshotStore snapshots, RunLogger log)
    {
        _adb = adb;
        _snapshots = snapshots;
        _log = log;
    }

    public async Task<ExecutionReport> ExecuteAsync(
        OptimizationPlan plan,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.Now;
        var notes = new List<string>();
        var selected = plan.SelectedItems.ToList();
        var results = new List<ExecutionItemResult>();

        if (selected.Count == 0)
        {
            notes.Add("没有勾选任何项目，未执行任何修改。");
            return new ExecutionReport
            {
                Device = plan.Device,
                Tier = plan.Tier,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.Now,
                Results = results,
                Notes = notes,
                LogPath = _log.Path,
            };
        }

        progress?.Report(new ExecutionProgress(0, selected.Count + 1, "正在记录可回滚快照…"));
        _log.Step($"开始执行：档位 {plan.Tier}，共 {selected.Count} 项。");

        var snapshot = await CaptureSnapshotAsync(plan, selected, ct).ConfigureAwait(false);
        var snapshotPath = _snapshots.Save(snapshot);
        _log.Success($"已保存回滚快照：{snapshotPath}");

        var index = 0;
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new ExecutionProgress(index, selected.Count + 1, $"正在处理：{item.DisplayName}", item));
            _log.Step($"[{index}/{selected.Count}] {item.ActionText} · {item.DisplayName}（{item.Target}）");
            var result = await ExecuteItemAsync(item, ct).ConfigureAwait(false);
            results.Add(result);
        }

        progress?.Report(new ExecutionProgress(selected.Count + 1, selected.Count + 1, "正在回读设备状态校验结果…"));
        await VerifyAsync(results, ct).ConfigureAwait(false);

        var report = new ExecutionReport
        {
            Device = plan.Device,
            Tier = plan.Tier,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            Results = results,
            Notes = notes,
            Snapshot = snapshot,
            SnapshotPath = snapshotPath,
            LogPath = _log.Path,
        };

        _log.Success($"执行结束：{report.Summary}，耗时 {report.Duration.TotalSeconds:0.0} 秒。");
        if (report.FailedCount > 0)
        {
            notes.Add($"有 {report.FailedCount} 项执行失败，具体原因见下方列表；失败项不影响其它项。");
        }
        notes.Add($"已保存回滚快照，随时可以在「回滚」页一键还原：{Path.GetFileName(snapshotPath)}");

        return report;
    }

    private async Task<SnapshotFile> CaptureSnapshotAsync(OptimizationPlan plan, IReadOnlyList<PlanItem> items, CancellationToken ct)
    {
        var entries = new List<SnapshotEntry>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Kind == PlanItemKind.Setting)
            {
                var before = new Dictionary<string, string?>();
                foreach (var key in item.SettingKeys ?? [])
                {
                    var value = await _adb.ShellTextAsync($"settings get {item.SettingNamespace} {key}", TimeSpan.FromSeconds(20), ct)
                        .ConfigureAwait(false);
                    before[$"{item.SettingNamespace}/{key}"] = NormalizeSettingValue(value);
                }

                entries.Add(new SnapshotEntry
                {
                    Kind = item.Kind,
                    Target = item.Target,
                    DisplayName = item.DisplayName,
                    ActionApplied = item.Action,
                    SettingsBefore = before,
                });
            }
            else
            {
                entries.Add(new SnapshotEntry
                {
                    Kind = item.Kind,
                    Target = item.Target,
                    DisplayName = item.DisplayName,
                    ActionApplied = item.Action,
                    PresenceBefore = item.PresenceBefore,
                    WasDisabled = item.PresenceBefore == PackagePresence.Disabled,
                    AppOps = item.AppOps,
                    StandbyBucket = item.StandbyBucket,
                });
            }
        }

        return new SnapshotFile
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.Now,
            DeviceModel = plan.Device.DisplayName,
            DeviceSerial = plan.Device.Serial,
            Brand = plan.Device.Brand,
            RomName = plan.Device.RomName,
            Tier = plan.Tier.ToString(),
            ListVersion = plan.ListVersion,
            Entries = entries,
        };
    }

    private async Task<ExecutionItemResult> ExecuteItemAsync(PlanItem item, CancellationToken ct)
    {
        try
        {
            return item.Kind == PlanItemKind.Setting
                ? await ApplySettingAsync(item, ct).ConfigureAwait(false)
                : item.Action switch
                {
                    PackageAction.Disable => await ApplyDisableAsync(item, ct).ConfigureAwait(false),
                    PackageAction.Uninstall => await ApplyUninstallAsync(item, ct).ConfigureAwait(false),
                    PackageAction.Restrict => await ApplyRestrictAsync(item, ct).ConfigureAwait(false),
                    _ => new ExecutionItemResult
                    {
                        Item = item,
                        Status = ActionStatus.Skipped,
                        Message = "该动作为「保留」，已跳过。",
                    },
                };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"{item.DisplayName} 执行异常：{ex.Message}");
            return new ExecutionItemResult
            {
                Item = item,
                Status = ActionStatus.Failed,
                Message = ex.Message,
                Hint = "请把日志文件发给我们以便定位问题。",
            };
        }
    }

    private async Task<ExecutionItemResult> ApplyDisableAsync(PlanItem item, CancellationToken ct)
    {
        var result = await _adb.ShellAsync($"pm disable-user --user 0 {item.Target}", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (IsCommandSuccess(result))
        {
            _log.Success($"已停用 {item.Target}");
            return new ExecutionItemResult
            {
                Item = item,
                Status = ActionStatus.Success,
                Message = "已停用该应用。",
                RawOutput = result.StdOut,
            };
        }

        // 个别老系统只支持 pm disable 语法。
        var legacy = await _adb.ShellAsync($"pm disable --user 0 {item.Target}", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (IsCommandSuccess(legacy))
        {
            _log.Success($"已停用（兼容命令）{item.Target}");
            return new ExecutionItemResult
            {
                Item = item,
                Status = ActionStatus.Success,
                Message = "已停用该应用（使用兼容命令）。",
                RawOutput = legacy.StdOut,
            };
        }

        _log.Error($"停用失败 {item.Target}：{result.Combined.Trim()}");
        return new ExecutionItemResult
        {
            Item = item,
            Status = ActionStatus.Failed,
            Message = "停用失败。",
            RawOutput = result.Combined.Trim(),
            Hint = BuildHint(result),
        };
    }

    private async Task<ExecutionItemResult> ApplyUninstallAsync(PlanItem item, CancellationToken ct)
    {
        var result = await _adb.ShellAsync($"pm uninstall -k --user 0 {item.Target}", TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        if (result.StdOut.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            _log.Success($"已卸载 {item.Target}");
            return new ExecutionItemResult
            {
                Item = item,
                Status = ActionStatus.Success,
                Message = "已为用户卸载（数据保留，可一键恢复）。",
                RawOutput = result.StdOut,
            };
        }

        _log.Warn($"卸载失败，降级为停用：{item.Target}");
        var fallback = await ApplyDisableAsync(item, ct).ConfigureAwait(false);
        var ok = fallback.Status == ActionStatus.Success;
        return fallback with
        {
            Status = ok ? ActionStatus.Downgraded : ActionStatus.Failed,
            Message = ok
                ? "该应用不允许卸载，已改为停用（效果相同，仍可一键恢复）。"
                : "卸载与停用都失败了。",
            RawOutput = $"{result.Combined.Trim()}\n---\n{fallback.RawOutput}",
        };
    }

    private async Task<ExecutionItemResult> ApplyRestrictAsync(PlanItem item, CancellationToken ct)
    {
        var applied = new List<string>();
        var failures = new List<string>();

        // StandbyBucket 为空表示这次只下发 appOps（例如传感器权限策略），不动后台策略。
        if (!string.IsNullOrWhiteSpace(item.StandbyBucket))
        {
            var bucketResult = await _adb.ShellAsync($"am set-standby-bucket {item.Target} {item.StandbyBucket}", TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(false);
            if (IsCommandSuccess(bucketResult))
            {
                applied.Add($"待机分桶→{item.StandbyBucket}");
            }
            else
            {
                var fallbackBucket = await _adb.ShellAsync($"am set-standby-bucket {item.Target} rare", TimeSpan.FromSeconds(30), ct)
                    .ConfigureAwait(false);
                if (IsCommandSuccess(fallbackBucket))
                {
                    applied.Add("待机分桶→rare");
                }
                else
                {
                    failures.Add($"待机分桶设置失败：{bucketResult.Combined.Trim()}");
                }
            }
        }

        foreach (var appOp in item.AppOps ?? [])
        {
            var opResult = await _adb.ShellAsync($"appops set {item.Target} {appOp.Op} {appOp.Mode}", TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(false);
            if (IsCommandSuccess(opResult))
            {
                applied.Add($"{appOp.Op}→{appOp.Mode}");
            }
            else
            {
                failures.Add($"{appOp.Op} 设置失败：{opResult.Combined.Trim()}");
            }
        }

        if (failures.Count == 0)
        {
            _log.Success($"已限制后台：{item.Target}（{string.Join("，", applied)}）");
            return new ExecutionItemResult
            {
                Item = item,
                Status = ActionStatus.Success,
                Message = $"已应用后台限制：{string.Join("，", applied)}。",
            };
        }

        if (applied.Count > 0)
        {
            _log.Warn($"部分限制生效 {item.Target}：{string.Join("；", failures)}");
            return new ExecutionItemResult
            {
                Item = item,
                Status = ActionStatus.Downgraded,
                Message = $"部分限制生效（{string.Join("，", applied)}）。",
                RawOutput = string.Join("\n", failures),
            };
        }

        _log.Error($"后台限制失败 {item.Target}：{string.Join("；", failures)}");
        return new ExecutionItemResult
        {
            Item = item,
            Status = ActionStatus.Failed,
            Message = "限制未生效，该应用或该系统不支持这些限制项。",
            RawOutput = string.Join("\n", failures),
            Hint = "国产定制系统对后台限制的支持不一致，这一项失败不影响其它优化。",
        };
    }

    private async Task<ExecutionItemResult> ApplySettingAsync(PlanItem item, CancellationToken ct)
    {
        var failures = new List<string>();
        var applied = 0;

        foreach (var key in item.SettingKeys ?? [])
        {
            var result = await _adb.ShellAsync($"settings put {item.SettingNamespace} {key} {item.SettingValue}", TimeSpan.FromSeconds(20), ct)
                .ConfigureAwait(false);
            if (IsCommandSuccess(result))
            {
                applied++;
            }
            else
            {
                failures.Add($"{key}：{result.Combined.Trim()}");
            }
        }

        if (failures.Count == 0)
        {
            _log.Success($"已修改设置 {item.DisplayName} = {item.SettingValue}");
            return new ExecutionItemResult
            {
                Item = item,
                Status = ActionStatus.Success,
                Message = $"已设置为 {item.SettingValue}。",
            };
        }

        return new ExecutionItemResult
        {
            Item = item,
            Status = applied > 0 ? ActionStatus.Downgraded : ActionStatus.Failed,
            Message = applied > 0 ? "部分设置项写入成功。" : "设置写入失败。",
            RawOutput = string.Join("\n", failures),
            Hint = "个别机型不允许通过 ADB 修改该设置项。",
        };
    }

    /// <summary>执行完后统一回读设备状态，给出「真的生效了吗」的结论。</summary>
    private async Task VerifyAsync(List<ExecutionItemResult> results, CancellationToken ct)
    {
        HashSet<string>? disabled = null;
        HashSet<string>? installed = null;

        try
        {
            disabled = await ListSetAsync("pm list packages -d --user 0", ct).ConfigureAwait(false);
            installed = await ListSetAsync("pm list packages --user 0", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"回读设备状态失败：{ex.Message}");
        }

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var item = result.Item;

            if (result.Status is ActionStatus.Failed or ActionStatus.Skipped)
            {
                continue;
            }

            if (item.Kind == PlanItemKind.Setting)
            {
                var ok = true;
                foreach (var key in item.SettingKeys ?? [])
                {
                    var current = NormalizeSettingValue(
                        await _adb.ShellTextAsync($"settings get {item.SettingNamespace} {key}", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false));
                    if (!string.Equals(current, item.SettingValue, StringComparison.OrdinalIgnoreCase))
                    {
                        ok = false;
                        break;
                    }
                }

                results[i] = result with
                {
                    Verified = ok,
                    VerifyDetail = ok ? "已回读确认生效。" : "回读的值与预期不一致，可能被系统改回。",
                };
                continue;
            }

            bool verified;
            if (item.Action == PackageAction.Restrict)
            {
                // 回读第一个 appOp 就能确认这批限制是否真的下发成功。
                verified = await VerifyAppOpAsync(item, ct).ConfigureAwait(false);
            }
            else
            {
                verified = item.Action switch
                {
                    PackageAction.Disable when disabled is not null => disabled.Contains(item.Target),
                    PackageAction.Uninstall when installed is not null => !installed.Contains(item.Target),
                    _ => false,
                };
            }

            results[i] = result with
            {
                Verified = verified,
                VerifyDetail = verified
                    ? (item.Action == PackageAction.Restrict ? "已回读确认限制生效。" : "已回读确认生效。")
                    : "回读状态与预期不一致，系统可能已自动恢复该项。",
            };
        }
    }

    private async Task<bool> VerifyAppOpAsync(PlanItem item, CancellationToken ct)
    {
        var appOp = item.AppOps?.FirstOrDefault();
        if (appOp is null)
        {
            // 只有待机分桶的项目，回读分桶状态。
            if (string.IsNullOrWhiteSpace(item.StandbyBucket)) return true;
            var bucket = await _adb.ShellTextAsync($"am get-standby-bucket {item.Target}", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            return bucket.Contains("restricted", StringComparison.OrdinalIgnoreCase)
                || bucket.Contains("rare", StringComparison.OrdinalIgnoreCase);
        }

        var result = await _adb.ShellAsync($"appops get {item.Target} {appOp.Op}", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (result.TimedOut) return false;
        return result.Combined.Contains(appOp.Mode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HashSet<string>> ListSetAsync(string command, CancellationToken ct)
    {
        var result = await _adb.ShellAsync(command, AdbClient.LongTimeout, ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.Lines)
        {
            var text = line.Trim();
            if (!text.StartsWith("package:", StringComparison.Ordinal)) continue;
            var name = text[8..].Trim();
            var marker = name.IndexOf(" versionCode:", StringComparison.Ordinal);
            if (marker > 0) name = name[..marker];
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    private static bool IsCommandSuccess(AdbResult result)
    {
        if (result.TimedOut) return false;
        if (result.ExitCode != 0) return false;
        if (result.LooksLikeFailure) return false;
        return true;
    }

    internal static string? NormalizeSettingValue(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static string BuildHint(AdbResult result)
    {
        var text = result.Combined;
        if (result.TimedOut)
        {
            return "命令超时。请点亮手机屏幕、保持解锁状态后重试。";
        }
        if (text.Contains("SecurityException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("requires", StringComparison.OrdinalIgnoreCase))
        {
            return "系统拒绝了这次操作。小米/红米需要在「开发者选项」里额外打开「USB 调试（安全设置）」，OPPO/vivo 需要在开发者选项中允许「通过 USB 修改权限」。";
        }
        if (text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return "权限不足。请在开发者选项中确认 USB 调试相关权限已全部打开。";
        }
        if (text.Contains("Unknown package", StringComparison.OrdinalIgnoreCase))
        {
            return "系统里找不到这个应用，可能已经被移除。";
        }
        return "可以稍后重试；如果反复失败，请把日志文件发给我们。";
    }
}
