using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

/// <summary>回滚时这一项到底能不能恢复。</summary>
public enum Restorability
{
    /// <summary>还没查（没连接手机，或者旧快照缺信息）。</summary>
    Unknown,
    /// <summary>能恢复。</summary>
    Restorable,
    /// <summary>已经恢复过了（当前就是装好的状态）。</summary>
    AlreadyOk,
    /// <summary>装不回来了，需要重新下载。</summary>
    NeedsRedownload,
}

/// <summary>
/// 判断某个「已卸载」的应用还能不能装回来。**只读**，不会改动手机。
///
/// 三种来源：电脑上的安装包备份 → 一定能装回；
/// 系统预装应用（系统分区上还有副本）→ install-existing 能装回；
/// 商店安装的应用且没备份 → 安装包已被系统删除，只能重新下载。
/// </summary>
public static class RestorabilityChecker
{
    public static async Task<(Restorability State, string Message)> CheckAsync(
        AdbClient adb,
        SnapshotFile snapshot,
        SnapshotEntry entry,
        CancellationToken ct = default)
    {
        // 只有「卸载」才需要判断能不能装回来；其它动作调用方自己给结论。
        if (entry.ActionApplied != PackageAction.Uninstall) return (Restorability.Unknown, string.Empty);

        var backup = ApkBackupStore.Find(snapshot.DeviceSerial, entry.Target);
        if (backup.Count > 0)
        {
            var size = ApkBackupStore.SizeOf(backup) / 1024.0 / 1024;
            return (Restorability.Restorable, $"可装回：电脑上有备份（{size:0.0} MB）");
        }

        // 已经装好了就不用恢复
        var listed = await adb.ShellAsync($"pm list packages --user 0 {entry.Target}", TimeSpan.FromSeconds(20), ct)
            .ConfigureAwait(false);
        if (listed.Lines.Any(l => l.Contains($"package:{entry.Target}", StringComparison.OrdinalIgnoreCase)))
        {
            return (Restorability.AlreadyOk, "已经装好了，不需要恢复");
        }

        var dump = await adb.ShellAsync($"dumpsys package {entry.Target}", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var text = dump.StdOut;
        if (!text.Contains("codePath=", StringComparison.Ordinal))
        {
            return (Restorability.NeedsRedownload, "⚠ 装不回来：手机里已经没有这个应用的记录，需要重新下载");
        }

        // 系统应用（含「系统应用被更新过」）在系统分区上还有副本，install-existing 能装回来
        if (text.Contains("UPDATED_SYSTEM_APP", StringComparison.Ordinal)
            || text.Contains("pkgFlags=[ SYSTEM", StringComparison.Ordinal))
        {
            return (Restorability.Restorable, "可装回：系统预装应用，系统里有内置副本");
        }

        // 非系统应用：看安装包目录里还有没有 apk
        var codePath = ExtractCodePath(text);
        if (codePath is not null)
        {
            var directory = codePath[..codePath.LastIndexOf('/')];
            var listing = await adb.ShellAsync($"ls {directory} 2>/dev/null", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (listing.StdOut.Contains(".apk", StringComparison.OrdinalIgnoreCase))
            {
                return (Restorability.Restorable, "可装回：手机里安装包还在");
            }
        }

        return (Restorability.NeedsRedownload, "⚠ 装不回来：卸载时没有备份，安装包已被系统删除，需要重新下载");
    }

    private static string? ExtractCodePath(string dump)
    {
        const string marker = "codePath=";
        var index = dump.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;

        var start = index + marker.Length;
        var end = dump.IndexOfAny(['\n', '\r'], start);
        var path = (end < 0 ? dump[start..] : dump[start..end]).Trim();
        return path.Contains('/') ? path : null;
    }
}
