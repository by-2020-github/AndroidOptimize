namespace AndroidOptimize.Core.Models;

public sealed record PackageEntry
{
    public required string Name { get; init; }
    public bool Installed { get; init; }
    public bool Disabled { get; init; }
    public bool RemovedForUser { get; init; }
    public bool IsSystem { get; init; }
    public bool IsThirdParty { get; init; }

    public string? VersionName { get; init; }
    public string? Installer { get; init; }

    /// <summary>应用在桌面上显示的名字（从 APK 里读出来的）。读不到就是 null。</summary>
    public string? Label { get; init; }

    public DateTimeOffset? FirstInstallTime { get; init; }
    public DateTimeOffset? LastUpdateTime { get; init; }
    public string? CodePath { get; init; }

    /// <summary>
    /// 系统记录的「安装后从未被打开过」标志（dumpsys package 里的 notLaunched）。
    /// true = 装完之后一次都没启动过，基本可以断定是被人强装/误装的；
    /// false = 用户确实用过；
    /// null = 这台设备的输出里没有这个字段，无从判断。
    /// </summary>
    public bool? NeverLaunched { get; init; }

    /// <summary>
    /// 这个应用在桌面上有没有图标（有 LAUNCHER 入口）。
    /// null = 没读到（例如关掉了深度扫描）。
    /// 「没有图标」是很硬的信号：用户根本打不开它，不可能是他日常在用的应用。
    /// </summary>
    public bool? HasLauncher { get; init; }

    /// <summary>
    /// 应用申请的权限列表（dumpsys package 里的 requested permissions 段）。
    /// 和安装来源一样，只有深度扫描到的应用才有；null = 没读到，不是「没有权限」。
    /// 用途是按权限筛查可疑应用，见 <see cref="PermissionAdvice"/>。
    /// </summary>
    public IReadOnlyList<string>? Permissions { get; init; }

    public PackagePresence Presence =>
        RemovedForUser ? PackagePresence.RemovedForUser
        : !Installed ? PackagePresence.RemovedForUser
        : Disabled ? PackagePresence.Disabled
        : PackagePresence.Installed;
}

public sealed class PackageSnapshot
{
    public required DeviceInfo Device { get; init; }
    public required DateTimeOffset ScannedAt { get; init; }
    public required IReadOnlyDictionary<string, PackageEntry> Packages { get; init; }
    public required int SystemCount { get; init; }
    public required int ThirdPartyCount { get; init; }
    public required bool DetailEnriched { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public PackageEntry? Find(string packageName) =>
        Packages.TryGetValue(packageName, out var entry) ? entry : null;

    public bool IsInstalled(string packageName) => Find(packageName)?.Installed == true;

    public IEnumerable<PackageEntry> InstalledPackages => Packages.Values.Where(p => p.Installed);
}
