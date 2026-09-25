using System.Diagnostics;
using System.Text;
using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Models;
using AndroidOptimize.Core.Services;

namespace AndroidOptimize.Core;

/// <summary>
/// 真机只读体检：连上手机跑一遍完整扫描，把「到底读到了什么」写成一份报告。
///
/// 全程只读（pm list / dumpsys / 读 APK 的局部字节），不会修改手机上的任何东西，
/// 连不上手机就直接报错返回。用途是排障——例如「权限列怎么是空的」——
/// 以及发版前在真机上做一次验证。
/// </summary>
public static class DeviceProbe
{
    public static async Task<string> RunAsync(
        string? appBaseDirectory = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool forceRefreshDetails = false)
    {
        var text = new StringBuilder();
        var rules = RuleRepository.Load(appBaseDirectory);
        var catalog = AppCatalog.Load(appBaseDirectory);

        text.AppendLine("安卓优化助手 · 真机只读体检");
        text.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"名单：{rules.ListVersion} · 离线名册：{catalog.Count} 条");
        text.AppendLine("（本命令只读，不会修改手机上的任何东西。）");
        text.AppendLine();

        AdbBootstrap.Ensure();
        var location = AdbLocator.Locate(appBaseDirectory: appBaseDirectory)
                       ?? throw new InvalidOperationException("没有找到 adb.exe，请确认程序目录下存在 bin\\adb.exe。");
        text.AppendLine($"ADB：{location.Path}（{location.Source}）");
        var cacheBefore = AppLabelCache.Load().Count;

        var adb = new AdbClient(location.Path);
        var device = await new DeviceScanner(adb, catalog: catalog).EnsureDeviceAsync(ct).ConfigureAwait(false);
        // 把扫描过程中的日志（尤其是「读不出应用名」的原因）收集下来，排障时一眼能看到。
        var scanLog = new List<string>();
        var scanner = new DeviceScanner(adb.ForDevice(device.Serial), catalog: catalog, log: message =>
        {
            lock (scanLog) scanLog.Add(message);
        });
        var info = await scanner.GetDeviceInfoAsync(ct).ConfigureAwait(false);

        text.AppendLine($"设备：{info.DisplayName}（{info.SystemSummary}）· 序列号 {info.Serial}");
        text.AppendLine();

        progress?.Report("正在扫描…（只读）");
        // 把扫描过程中的每一步都记下来，用来判断「为什么第二次扫描还是这么慢」。
        var progressLog = new List<string>();
        var collecting = new Progress<string>(message =>
        {
            lock (progressLog) progressLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            progress?.Report(message);
        });
        var stopwatch = Stopwatch.StartNew();
        // 默认和界面一致（走缓存）；排障想看手机上此刻的真实状态时加 --force。
        var snapshot = await scanner
            .ScanPackagesAsync(info, deepScan: true, forceRefreshDetails, collecting, ct)
            .ConfigureAwait(false);
        stopwatch.Stop();

        // 顺手归档：给多台手机做优化时，跑这一条命令就能把「机型 + 应用 + 权限」积累下来。
        try
        {
            var archivePath = DeviceArchive.SaveScan(info, snapshot, rules, catalog);
            text.AppendLine($"本次扫描已归档：{archivePath}");
        }
        catch (Exception ex)
        {
            text.AppendLine($"扫描归档失败（不影响本次结果）：{ex.Message}");
        }

        var installed = snapshot.Packages.Values.Where(p => p.Installed).ToList();
        text.AppendLine("===== 扫描结果 =====");
        text.AppendLine($"耗时：{stopwatch.Elapsed.TotalSeconds:0.0} 秒");
        text.AppendLine($"已安装 {installed.Count} 个（系统 {snapshot.SystemCount} / 第三方 {snapshot.ThirdPartyCount}）");
        text.AppendLine($"深度扫描（安装来源 + 权限）：{(snapshot.DetailEnriched ? "已执行" : "**没有执行**")}");
        foreach (var warning in snapshot.Warnings)
        {
            text.AppendLine($"  警告：{warning}");
        }
        text.AppendLine();
        text.AppendLine("扫描过程：");
        lock (progressLog)
        {
            foreach (var line in progressLog) text.AppendLine("  " + line);
        }
        text.AppendLine();

        var withPermissions = installed.Where(p => p.Permissions is not null).ToList();
        var withoutPermissions = installed.Where(p => p.Permissions is null).ToList();

        var cacheAfter = AppLabelCache.Load().Count;
        var labelled = installed.Count(p => !string.IsNullOrWhiteSpace(p.Label));
        text.AppendLine("===== 应用名缓存 =====");
        text.AppendLine($"缓存文件：{AppPaths.LabelCacheFile}");
        text.AppendLine($"扫描前 {cacheBefore} 条 → 扫描后 {cacheAfter} 条");
        text.AppendLine($"本次扫描后有名字的应用：{labelled} / {installed.Count}");
        lock (scanLog)
        {
            foreach (var line in scanLog.Where(l => l.StartsWith("应用名", StringComparison.Ordinal)))
            {
                text.AppendLine("  " + line);
            }

            var failures = scanLog.Where(l => l.Contains("的应用名失败", StringComparison.Ordinal)).ToList();
            if (failures.Count > 0)
            {
                text.AppendLine($"读不出应用名的：{failures.Count} 个（原因归类）");
                foreach (var group in failures
                             .GroupBy(l => l[(l.IndexOf('：') + 1)..])
                             .OrderByDescending(g => g.Count())
                             .Take(10))
                {
                    text.AppendLine($"  {group.Count(),4} 个 × {group.Key}");
                }
                foreach (var line in failures.Take(25)) text.AppendLine("  · " + line);
                if (failures.Count > 25) text.AppendLine($"  ……还有 {failures.Count - 25} 条");
            }
        }
        text.AppendLine();

        text.AppendLine("===== 权限读取情况 =====");
        text.AppendLine($"读到权限的应用：{withPermissions.Count} 个");
        text.AppendLine($"没读到（界面显示「—」）：{withoutPermissions.Count} 个");
        text.AppendLine($"其中第三方应用没读到的：{withoutPermissions.Count(p => p.IsThirdParty)} 个");
        text.AppendLine($"没读到安装来源的第三方应用：{installed.Count(p => p.IsThirdParty && string.IsNullOrWhiteSpace(p.Installer))} 个");
        text.AppendLine();

        var profiles = withPermissions
            .Select(p => (Entry: p, Profile: PermissionAdvice.Build(
                p.Permissions, rules.RuleSet.PermissionRisks, rules.RuleSet.PermissionCombos)))
            .ToList();
        var risky = profiles.Where(x => x.Profile.HasHighRisk).ToList();

        text.AppendLine($"含高危权限的应用：{risky.Count} 个");
        foreach (var combo in rules.RuleSet.PermissionCombos)
        {
            var comboCount = profiles.Count(x => x.Profile.ComboHits.Any(c => c.Name == combo.Name));
            text.AppendLine($"   其中命中「{combo.Name}」：{comboCount} 个");
        }
        text.AppendLine();
        text.AppendLine("各条权限的命中数（用来判断名单里的风险等级划得准不准）：");
        var ranking = profiles
            .SelectMany(x => x.Profile.Hits)
            .GroupBy(h => (h.Name, h.Risk))
            .Select(g => (g.Key.Name, g.Key.Risk, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();
        foreach (var (name, risk, count) in ranking)
        {
            text.AppendLine($"  {name,-12} {RiskText(risk)}  {count,4} 个");
        }
        text.AppendLine();

        foreach (var (entry, profile) in risky.Take(40).OrderByDescending(x => x.Entry.IsThirdParty))
        {
            text.AppendLine($"  · {entry.Label ?? entry.Name}（{entry.Name}）");
            text.AppendLine($"      权限：{profile.DisplayText}");
        }
        if (risky.Count > 40) text.AppendLine($"  ……还有 {risky.Count - 40} 个");
        text.AppendLine();

        text.AppendLine("===== 没读到权限的第三方应用（前 20 个）=====");
        foreach (var entry in withoutPermissions.Where(p => p.IsThirdParty).Take(20))
        {
            text.AppendLine($"  · {entry.Label ?? entry.Name}（{entry.Name}）");
        }

        text.AppendLine();
        text.AppendLine("报告结束。这份文件可以直接发给开发者用于排障。");
        return text.ToString();
    }

    private static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.High => "高",
        RiskLevel.Medium => "中",
        _ => "低",
    };
}
