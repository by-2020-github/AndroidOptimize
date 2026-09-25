using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

/// <summary>
/// 按手机序列号归档每次扫描与执行的结果。
///
/// 目的：给多台手机做优化时，数据不再全堆在一个目录里，而是
/// <c>devices\&lt;序列号&gt;\</c> 一台一份；积累到一定量之后，
/// <see cref="BuildStatistics"/> 能算出「哪些应用最常出现、哪些权限最常见、
/// 名单外的应用有哪些」——这些就是改进名单和程序的依据。
///
/// 所有文件都只写在本机（%LOCALAPPDATA%\AndroidOptimize），不上传任何地方。
/// </summary>
public static class DeviceArchive
{
    /// <summary>每台手机最多保留多少次扫描记录（按时间，新的留着）。</summary>
    public const int MaxScanRecords = 10;

    /// <summary>每台手机最多保留多少条执行记录。</summary>
    public const int MaxExecutionRecords = 20;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// 所有公开方法都接受一个可选的 devicesRoot：
    /// 自检用它写到临时目录，避免把测试设备混进用户真实的手机档案里。
    /// </summary>
    public static string ProfilePath(string serial, string? devicesRoot = null) =>
        Path.Combine(DeviceDirectory(serial, devicesRoot), "device.json");

    private static string DeviceDirectory(string serial, string? devicesRoot) =>
        Path.Combine(devicesRoot ?? AppPaths.DevicesDir,
            AppPaths.Sanitize(string.IsNullOrWhiteSpace(serial) ? "未知设备" : serial));

    private static string ScansDirectory(string serial, string? devicesRoot) =>
        Path.Combine(DeviceDirectory(serial, devicesRoot), "scans");

    private static string ExecutionsDirectory(string serial, string? devicesRoot) =>
        Path.Combine(DeviceDirectory(serial, devicesRoot), "executions");

    public static DeviceProfile? LoadProfile(string serial, string? devicesRoot = null)
    {
        try
        {
            var path = ProfilePath(serial, devicesRoot);
            return File.Exists(path) ? JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(path), Options) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把一次扫描的结果写进这台手机的档案。返回扫描记录文件路径。</summary>
    public static string SaveScan(
        DeviceInfo device,
        PackageSnapshot snapshot,
        RuleRepository rules,
        AppCatalog? catalog = null,
        string? devicesRoot = null)
    {
        var serial = string.IsNullOrWhiteSpace(device.Serial) ? "未知设备" : device.Serial;
        var directory = ScansDirectory(serial, devicesRoot);
        Directory.CreateDirectory(directory);

        var packages = new List<DeviceScanPackage>();
        foreach (var entry in snapshot.Packages.Values.Where(p => p.Installed))
        {
            var rule = rules.FindPackageRule(entry.Name, device.BrandTokens);
            var protection = rules.MatchProtection(entry.Name);
            var catalogEntry = catalog?.Find(entry.Name);
            var (adviceText, _) = Advice.ForPackage(rule, protection, catalogEntry, entry.HasLauncher);

            packages.Add(new DeviceScanPackage
            {
                Name = entry.Name,
                Label = entry.Label,
                Version = entry.VersionName,
                IsSystem = entry.IsSystem,
                Installer = entry.Installer,
                HasLauncher = entry.HasLauncher,
                NeverLaunched = entry.NeverLaunched,
                FirstInstallTime = entry.FirstInstallTime,
                Advice = adviceText,
                Removal = catalogEntry?.Removal,
                RuleName = rule?.DisplayName,
                RuleAction = rule is null ? null : rule.Action.ToString(),
                Protection = protection.IsProtected ? $"{protection.Pattern.Category}:{protection.Level}" : null,
                Permissions = entry.Permissions,
            });
        }

        var record = new DeviceScanRecord
        {
            ScannedAt = snapshot.ScannedAt,
            Serial = serial,
            Brand = device.Brand,
            Model = device.Model,
            RomName = device.RomName,
            RomVersion = device.RomVersion,
            AndroidRelease = device.AndroidRelease,
            SdkInt = device.SdkInt,
            ListVersion = rules.ListVersion,
            InstalledCount = packages.Count,
            SystemCount = snapshot.SystemCount,
            ThirdPartyCount = snapshot.ThirdPartyCount,
            DetailEnriched = snapshot.DetailEnriched,
            Packages = packages,
        };

        var path = Path.Combine(directory, $"{snapshot.ScannedAt:yyyyMMdd-HHmmss}.json");
        WriteAtomic(path, JsonSerializer.Serialize(record, Options));
        TrimOldFiles(directory, MaxScanRecords);

        UpdateProfile(device, packages.Count, snapshot.ThirdPartyCount, countScan: true, countExecution: false, devicesRoot);
        return path;
    }

    /// <summary>把一次执行结果写进这台手机的档案。返回执行记录文件路径。</summary>
    public static string SaveExecution(
        DeviceInfo device,
        OptimizationPlan plan,
        ExecutionReport report,
        string? devicesRoot = null)
    {
        var serial = string.IsNullOrWhiteSpace(device.Serial) ? "未知设备" : device.Serial;
        var directory = ExecutionsDirectory(serial, devicesRoot);
        Directory.CreateDirectory(directory);

        var record = new DeviceExecutionRecord
        {
            ExecutedAt = DateTimeOffset.Now,
            Serial = serial,
            Tier = plan.Tier.ToString(),
            Summary = report.Summary,
            Actions = report.Results.Select(r => new DeviceExecutionAction
            {
                Target = r.Item.Target,
                DisplayName = r.Item.DisplayName,
                Action = r.Item.Action.ToString(),
                Status = r.Status.ToString(),
                Verified = r.Verified,
                Message = r.Message,
            }).ToList(),
        };

        var path = Path.Combine(directory, $"{record.ExecutedAt:yyyyMMdd-HHmmss}.json");
        WriteAtomic(path, JsonSerializer.Serialize(record, Options));
        TrimOldFiles(directory, MaxExecutionRecords);

        UpdateProfile(device, plan.PackageCount, thirdParty: 0, countScan: false, countExecution: true, devicesRoot);
        return path;
    }

    public static IReadOnlyList<DeviceProfile> ListDevices(string? devicesRoot = null)
    {
        var result = new List<DeviceProfile>();
        var root = devicesRoot ?? AppPaths.DevicesDir;
        if (!Directory.Exists(root)) return result;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var profile = LoadProfile(Path.GetFileName(directory), devicesRoot);
            if (profile is not null) result.Add(profile);
        }
        return result.OrderByDescending(p => p.LastSeenAt).ToList();
    }

    /// <summary>读一台手机最近一次的扫描记录。</summary>
    public static DeviceScanRecord? LoadLatestScan(string serial, string? devicesRoot = null)
    {
        var directory = ScansDirectory(serial, devicesRoot);
        if (!Directory.Exists(directory)) return null;

        var latest = Directory.EnumerateFiles(directory, "*.json")
            .OrderByDescending(Path.GetFileName)
            .FirstOrDefault();
        if (latest is null) return null;

        try
        {
            return JsonSerializer.Deserialize<DeviceScanRecord>(File.ReadAllText(latest), Options);
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateProfile(
        DeviceInfo device,
        int installedCount,
        int thirdParty,
        bool countScan,
        bool countExecution,
        string? devicesRoot)
    {
        var serial = string.IsNullOrWhiteSpace(device.Serial) ? "未知设备" : device.Serial;
        var existing = LoadProfile(serial, devicesRoot);
        var now = DateTimeOffset.Now;

        var profile = existing ?? new DeviceProfile
        {
            Serial = serial,
            FirstSeenAt = now,
        };

        profile.Brand = device.Brand;
        profile.Manufacturer = device.Manufacturer;
        profile.Model = device.Model;
        profile.Device = device.Device;
        profile.AndroidRelease = device.AndroidRelease;
        profile.SdkInt = device.SdkInt;
        profile.RomName = device.RomName;
        profile.RomVersion = device.RomVersion;
        profile.LastSeenAt = now;
        if (countScan)
        {
            profile.ScanCount++;
            profile.InstalledCount = installedCount;
            profile.ThirdPartyCount = thirdParty;
        }
        if (countExecution) profile.ExecutionCount++;

        Directory.CreateDirectory(DeviceDirectory(serial, devicesRoot));
        WriteAtomic(ProfilePath(serial, devicesRoot), JsonSerializer.Serialize(profile, Options));
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void TrimOldFiles(string directory, int keep)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(directory, "*.json")
                         .OrderByDescending(Path.GetFileName)
                         .Skip(keep))
            {
                File.Delete(stale);
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }

    /// <summary>
    /// 把所有手机的档案汇总成一份统计报告，用来改进名单与程序。
    /// **不含序列号**：设备只用「设备 1/2/3」编号，避免这份文件流出去时带上设备标识。
    /// </summary>
    public static string BuildStatistics(string? appBaseDirectory = null, string? devicesRoot = null)
    {
        var rules = RuleRepository.Load(appBaseDirectory);
        var profiles = ListDevices(devicesRoot);
        var scans = new List<(DeviceProfile Profile, DeviceScanRecord Record)>();
        foreach (var profile in profiles)
        {
            var record = LoadLatestScan(profile.Serial, devicesRoot);
            if (record is not null) scans.Add((profile, record));
        }

        var text = new StringBuilder();
        text.AppendLine("# 安卓优化助手 · 数据统计");
        text.AppendLine();
        text.AppendLine($"> 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　设备数：{profiles.Count}（有扫描记录 {scans.Count}）");
        text.AppendLine(">");
        text.AppendLine("> **这份文件不含手机序列号、联系人、短信等任何个人信息**，只有机型、系统版本、包名与权限名。");
        text.AppendLine("> 每台手机只统计最近一次扫描，避免反复优化同一台手机时把它的数据算重。");
        text.AppendLine();

        if (scans.Count == 0)
        {
            text.AppendLine("还没有任何手机的扫描记录。连接手机扫描一次即可。");
            return text.ToString();
        }

        AppendDeviceTable(text, scans);
        AppendAdviceTable(text, scans);
        AppendUnknownPackages(text, scans, rules);
        AppendPermissionRanking(text, scans, rules);
        AppendUnlistedPermissions(text, scans, rules);
        AppendComboRanking(text, scans, rules);
        AppendNeverLaunched(text, scans);
        return text.ToString();
    }

    private static void AppendDeviceTable(StringBuilder text, List<(DeviceProfile Profile, DeviceScanRecord Record)> scans)
    {
        text.AppendLine("## 一、机型与系统版本");
        text.AppendLine();
        text.AppendLine("| 设备 | 品牌 | 型号 | 系统 | Android | 已安装 | 第三方 | 扫描次数 |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        var index = 0;
        foreach (var (profile, record) in scans.OrderByDescending(s => s.Record.ScannedAt))
        {
            index++;
            text.AppendLine($"| 设备 {index} | {record.Brand} | {record.Model} | {record.RomName} {record.RomVersion} | " +
                            $"{record.AndroidRelease} (API {record.SdkInt}) | {record.InstalledCount} | {record.ThirdPartyCount} | {profile.ScanCount} |");
        }
        text.AppendLine();
    }

    private static void AppendAdviceTable(StringBuilder text, List<(DeviceProfile Profile, DeviceScanRecord Record)> scans)
    {
        text.AppendLine("## 二、建议分布（界面「建议」列的口径）");
        text.AppendLine();
        text.AppendLine("| 建议 | 出现在几台手机 | 总计出现次数 |");
        text.AppendLine("|---|---|---|");

        var byAdvice = scans
            .SelectMany(s => s.Record.Packages)
            .Where(p => !string.IsNullOrWhiteSpace(p.Advice))
            .GroupBy(p => p.Advice!)
            .Select(g => (Advice: g.Key, Devices: scans.Count(s => s.Record.Packages.Any(p => p.Advice == g.Key)), Count: g.Count()))
            .OrderByDescending(x => x.Count);

        foreach (var (advice, devices, count) in byAdvice)
        {
            text.AppendLine($"| {advice} | {devices} | {count} |");
        }
        text.AppendLine();
    }

    /// <summary>名单外 + 名册里也没有的应用：这是最该补名单的一批。</summary>
    private static void AppendUnknownPackages(
        StringBuilder text,
        List<(DeviceProfile Profile, DeviceScanRecord Record)> scans,
        RuleRepository rules)
    {
        text.AppendLine("## 三、名单外的第三方应用（建议评估后加进名单）");
        text.AppendLine();
        text.AppendLine("这些包既不在 `packages.json` 里，也没被离线名册收录，每次都要用户自己判断。");
        text.AppendLine();
        text.AppendLine("| 应用名 | 包名 | 出现在几台手机 | 安装来源 | 从未启动过 |");
        text.AppendLine("|---|---|---|---|---|");

        var thirdPartyUnknown = scans
            .SelectMany(s => s.Record.Packages
                .Where(p => !p.IsSystem && p.RuleName is null && p.Removal is null)
                .Select(p => (Device: s.Profile.Serial, p.Name, p.Label, p.Installer, p.NeverLaunched)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                Name: g.Key,
                Label: g.Select(x => x.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)),
                Devices: g.Select(x => x.Device).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Installer: g.Select(x => x.Installer).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)),
                NeverLaunched: g.All(x => x.NeverLaunched == true)))
            .OrderByDescending(x => x.Devices)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        if (thirdPartyUnknown.Count == 0)
        {
            text.AppendLine("| （没有） | | | | |");
        }
        foreach (var row in thirdPartyUnknown)
        {
            text.AppendLine($"| {row.Label ?? "-"} | `{row.Name}` | {row.Devices} | {row.Installer ?? "-"} | {(row.NeverLaunched ? "是" : "-")} |");
        }
        text.AppendLine();
    }

    private static void AppendPermissionRanking(
        StringBuilder text,
        List<(DeviceProfile Profile, DeviceScanRecord Record)> scans,
        RuleRepository rules)
    {
        text.AppendLine("## 四、权限命中排行（用来校准 permissionRisks）");
        text.AppendLine();
        text.AppendLine("| 权限 | 名单里的等级 | 命中应用数 | 占已读权限应用的比例 |");
        text.AppendLine("|---|---|---|---|");

        var withPermissions = scans.SelectMany(s => s.Record.Packages)
            .Where(p => p.Permissions is { Count: > 0 })
            .ToList();
        if (withPermissions.Count == 0)
        {
            text.AppendLine("| （没有读到权限） | | | |");
            text.AppendLine();
            return;
        }

        foreach (var risk in rules.RuleSet.PermissionRisks)
        {
            var hits = withPermissions.Count(p => p.Permissions!.Any(x =>
                x.Contains(risk.Match, StringComparison.OrdinalIgnoreCase)));
            if (hits == 0) continue;
            var percent = hits * 100.0 / withPermissions.Count;
            text.AppendLine($"| {risk.Name} | {risk.Risk} | {hits} | {percent:0.0}% |");
        }
        text.AppendLine();
    }

    /// <summary>出现得多、但名单里还没收录的权限——新 permissionRisks 的候选。</summary>
    private static void AppendUnlistedPermissions(
        StringBuilder text,
        List<(DeviceProfile Profile, DeviceScanRecord Record)> scans,
        RuleRepository rules)
    {
        text.AppendLine("## 五、还没收录的权限（已滤掉日常权限，permissionRisks 的候选）");
        text.AppendLine();

        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var packagesWithPermissions = 0;
        var everydaySkipped = 0;
        foreach (var package in scans.SelectMany(s => s.Record.Packages))
        {
            if (package.Permissions is not { Count: > 0 }) continue;
            packagesWithPermissions++;

            foreach (var permission in package.Permissions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!permission.Contains(".permission.", StringComparison.OrdinalIgnoreCase)) continue;
                if (rules.RuleSet.PermissionRisks.Any(r => permission.Contains(r.Match, StringComparison.OrdinalIgnoreCase))) continue;
                if (IsEverydayPermission(permission))
                {
                    everydaySkipped++;
                    continue;
                }

                counters[permission] = counters.TryGetValue(permission, out var count) ? count + 1 : 1;
            }
        }

        text.AppendLine("> 「日常权限」指联网、相机、麦克风、定位、存储、蓝牙、NFC、震动、媒体读取这类" +
                        "几乎每个应用都会要的权限，它们不合适拿来判断「这个应用可疑」。");
        text.AppendLine();
        text.AppendLine("| 权限 | 申请它的应用数 |");
        text.AppendLine("|---|---|");
        foreach (var (permission, count) in counters.OrderByDescending(kv => kv.Value).Take(40))
        {
            text.AppendLine($"| `{permission}` | {count} |");
        }
        text.AppendLine();
        text.AppendLine($"（共 {packagesWithPermissions} 个应用读到了权限列表；已滤掉 {everydaySkipped} 条日常权限记录）");
        text.AppendLine();
    }

    /// <summary>
    /// 几乎每个应用都会申请的权限。它们出现在这里只是噪音，
    /// 真正值得看的是「很少见、但一看就不对劲」的那些（WRITE_SECURE_SETTINGS、GET_TASKS…）。
    /// </summary>
    private static readonly string[] EverydayPermissions =
    [
        "permission.INTERNET", "ACCESS_NETWORK_STATE", "ACCESS_WIFI_STATE", "CHANGE_WIFI_STATE",
        "CHANGE_NETWORK_STATE", "ACCESS_COARSE_LOCATION", "ACCESS_FINE_LOCATION", "ACCESS_MEDIA_LOCATION",
        "permission.CAMERA", "RECORD_AUDIO", "MODIFY_AUDIO_SETTINGS", "permission.VIBRATE",
        "READ_EXTERNAL_STORAGE", "WRITE_EXTERNAL_STORAGE", "MANAGE_EXTERNAL_STORAGE", "READ_MEDIA_",
        "ACCESS_MEDIA_", "permission.NFC", "permission.BLUETOOTH", "BLUETOOTH_CONNECT", "BLUETOOTH_SCAN",
        "BLUETOOTH_ADVERTISE", "USE_FINGERPRINT", "USE_BIOMETRIC", "FOREGROUND_SERVICE", "POST_NOTIFICATIONS",
        "permission.WAKE_LOCK", "READ_APP_BADGE", "CHANGE_BADGE", "UPDATE_BADGE", "BROADCAST_BADGE",
        "RECEIVE_USER_PRESENT", "READ_SETTINGS", "SetUnreadBadge", "SEND_PUSH_MESSAGE", "SEND_MCS_MESSAGE",
    ];

    private static bool IsEverydayPermission(string permission) =>
        EverydayPermissions.Any(everyday => permission.Contains(everyday, StringComparison.OrdinalIgnoreCase));

    private static void AppendComboRanking(
        StringBuilder text,
        List<(DeviceProfile Profile, DeviceScanRecord Record)> scans,
        RuleRepository rules)
    {
        text.AppendLine("## 六、命中危险组合的应用");
        text.AppendLine();

        var hits = new List<(string Combo, string Package, string? Label, string Device)>();
        foreach (var (profile, record) in scans)
        {
            foreach (var package in record.Packages)
            {
                if (package.Permissions is not { Count: > 0 }) continue;
                foreach (var combo in rules.RuleSet.PermissionCombos)
                {
                    if (combo.AllOf.Count == 0) continue;
                    if (!combo.AllOf.All(keyword => package.Permissions.Any(p =>
                            p.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
                    {
                        continue;
                    }
                    hits.Add((combo.Name, package.Name, package.Label, profile.Serial));
                }
            }
        }

        if (hits.Count == 0)
        {
            text.AppendLine("（没有）");
            text.AppendLine();
            return;
        }

        text.AppendLine("| 组合 | 应用名 | 包名 | 出现在几台手机 |");
        text.AppendLine("|---|---|---|---|");
        foreach (var row in hits
                     .GroupBy(h => (h.Combo, h.Package))
                     .Select(g => (
                         g.Key.Combo,
                         g.Key.Package,
                         Label: g.Select(x => x.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)),
                         Devices: g.Select(x => x.Device).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
                     .OrderByDescending(x => x.Devices)
                     .ThenBy(x => x.Combo, StringComparer.Ordinal)
                     .Take(40))
        {
            text.AppendLine($"| {row.Combo} | {row.Label ?? "-"} | `{row.Package}` | {row.Devices} |");
        }
        text.AppendLine();
    }

    private static void AppendNeverLaunched(StringBuilder text, List<(DeviceProfile Profile, DeviceScanRecord Record)> scans)
    {
        text.AppendLine("## 七、系统记录「从未启动过」的应用");
        text.AppendLine();
        text.AppendLine("> 注意：被其它应用唤醒也算启动过，所以这个字段只能当参考，不能当删除依据。");
        text.AppendLine();
        text.AppendLine("| 应用名 | 包名 | 出现在几台手机 | 名单状态 |");
        text.AppendLine("|---|---|---|---|");

        var rows = scans
            .SelectMany(s => s.Record.Packages
                .Where(p => p.NeverLaunched == true)
                .Select(p => (Device: s.Profile.Serial, Package: p)))
            .GroupBy(x => x.Package.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                g.Key,
                Label: g.Select(x => x.Package.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)),
                Devices: g.Select(x => x.Device).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                State: g.Select(x => x.Package.RuleName ?? x.Package.Removal ?? "名单外").First()))
            .OrderByDescending(x => x.Devices)
            .Take(30);

        foreach (var row in rows)
        {
            text.AppendLine($"| {row.Label ?? "-"} | `{row.Key}` | {row.Devices} | {row.State} |");
        }
        text.AppendLine();
    }
}
