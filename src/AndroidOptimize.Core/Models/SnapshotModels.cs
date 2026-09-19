namespace AndroidOptimize.Core.Models;

public sealed record SnapshotEntry
{
    public required PlanItemKind Kind { get; init; }
    public required string Target { get; init; }
    public required string DisplayName { get; init; }
    public required PackageAction ActionApplied { get; init; }
    public PackagePresence PresenceBefore { get; init; } = PackagePresence.Unknown;
    public bool WasDisabled { get; init; }

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
