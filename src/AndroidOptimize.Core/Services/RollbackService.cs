using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

/// <summary>按快照把设备还原到优化之前的状态。逐项尽力恢复，单项失败不影响其它项。</summary>
public sealed class RollbackService
{
    private readonly AdbClient _adb;
    private readonly RunLogger _log;

    public RollbackService(AdbClient adb, RunLogger log)
    {
        _adb = adb;
        _log = log;
    }

    public async Task<RollbackReport> RestoreAsync(
        SnapshotFile snapshot,
        string snapshotPath,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<RollbackItemResult>();
        var entries = snapshot.Entries;
        var index = 0;

        _log.Step($"开始回滚：{snapshot.DisplayName}，共 {entries.Count} 项。");

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new ExecutionProgress(index, entries.Count, $"正在还原：{entry.DisplayName}"));

            var (success, message) = entry.Kind == PlanItemKind.Setting
                ? await RestoreSettingAsync(entry, ct).ConfigureAwait(false)
                : await RestorePackageAsync(entry, ct).ConfigureAwait(false);

            results.Add(new RollbackItemResult(entry.Target, entry.DisplayName, success, message));
            if (success) _log.Success($"已还原 {entry.DisplayName}");
            else _log.Error($"还原失败 {entry.DisplayName}：{message}");
        }

        progress?.Report(new ExecutionProgress(entries.Count, entries.Count, "回滚完成。"));
        return new RollbackReport { SnapshotPath = snapshotPath, Results = results };
    }

    private async Task<(bool Success, string Message)> RestoreSettingAsync(SnapshotEntry entry, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var (key, value) in entry.SettingsBefore)
        {
            var separator = key.IndexOf('/');
            if (separator <= 0)
            {
                continue;
            }

            var ns = key[..separator];
            var name = key[(separator + 1)..];
            var command = value is null
                ? $"settings delete {ns} {name}"
                : $"settings put {ns} {name} {value}";

            var result = await _adb.ShellAsync(command, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (!IsSuccess(result))
            {
                failures.Add($"{key}：{result.Combined.Trim()}");
            }
        }

        return failures.Count == 0
            ? (true, "设置已还原为原值。")
            : (false, string.Join("；", failures));
    }

    private async Task<(bool Success, string Message)> RestorePackageAsync(SnapshotEntry entry, CancellationToken ct)
    {
        var messages = new List<string>();
        var failed = false;

        if (entry.ActionApplied == PackageAction.Restrict)
        {
            var bucket = await _adb.ShellAsync($"am set-standby-bucket {entry.Target} active", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!IsSuccess(bucket)) failed = true;
            else messages.Add("后台限制已解除");

            foreach (var appOp in entry.AppOps ?? [])
            {
                var op = await _adb.ShellAsync($"appops set {entry.Target} {appOp.Op} default", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (!IsSuccess(op)) failed = true;
            }

            return failed
                ? (false, "部分后台限制未能解除。")
                : (true, "已解除后台限制。");
        }

        if (entry.ActionApplied == PackageAction.Uninstall)
        {
            var install = await _adb.ShellAsync($"cmd package install-existing --user 0 {entry.Target}", TimeSpan.FromSeconds(90), ct)
                .ConfigureAwait(false);
            var text = install.StdOut;
            if (IsSuccess(install) || text.Contains("installed for user", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add("已恢复安装");
            }
            else
            {
                failed = true;
                messages.Add($"恢复安装失败：{install.Combined.Trim()}");
            }
        }

        if (!entry.WasDisabled)
        {
            var enable = await _adb.ShellAsync($"pm enable --user 0 {entry.Target}", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (IsSuccess(enable))
            {
                messages.Add("已启用");
            }
            else if (entry.ActionApplied == PackageAction.Disable)
            {
                failed = true;
                messages.Add($"启用失败：{enable.Combined.Trim()}");
            }
        }

        if (messages.Count == 0)
        {
            return (true, "无需还原。");
        }

        return failed
            ? (false, string.Join("；", messages))
            : (true, string.Join("；", messages) + "。");
    }

    private static bool IsSuccess(AdbResult result) => !result.TimedOut && result.ExitCode == 0 && !result.LooksLikeFailure;
}
