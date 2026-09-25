namespace AndroidOptimize.Core.Models;

public sealed record SnapshotEntry
{
    public required PlanItemKind Kind { get; init; }
    public required string Target { get; init; }
    public required string DisplayName { get; init; }
    public required PackageAction ActionApplied { get; init; }
    public PackagePresence PresenceBefore { get; init; } = PackagePresence.Unknown;
    public bool WasDisabled { get; init; }

    /// <summary>执行时这个包是不是系统预装应用（决定卸载后还能不能用 install-existing 装回来）。</summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// 卸载前备份的安装包目录（相对用户数据目录）。空 = 没备份。
    /// 商店安装的应用卸载后安装包会被系统删掉，只有备份才能装回来。
    /// </summary>
    public string? ApkBackupDirectory { get; init; }

    /// <summary>备份占用的字节数，界面上显示给用户看。</summary>
    public long ApkBackupBytes { get; init; }

    /// <summary>设置项修改前的原始值，key 为 "namespace/key"。</summary>
    public Dictionary<string, string?> SettingsBefore { get; init; } = new();

    public IReadOnlyList<AppOpRule>? AppOps { get; init; }
    public string? StandbyBucket { get; init; }
}

public sealed record SnapshotFile
{
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string DeviceModel { get; init; }
    public required string DeviceSerial { get; init; }
    public required string Brand { get; init; }
    public required string RomName { get; init; }
    public required string Tier { get; init; }
    public required string ListVersion { get; init; }
    public required IReadOnlyList<SnapshotEntry> Entries { get; init; }

    public string DisplayName => $"{CreatedAt:yyyy-MM-dd HH:mm} · {DeviceModel} · {Entries.Count} 项";
}

public sealed record SnapshotSummary(string Path, SnapshotFile Snapshot);
