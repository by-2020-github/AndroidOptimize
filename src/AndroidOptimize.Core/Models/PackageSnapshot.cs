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
