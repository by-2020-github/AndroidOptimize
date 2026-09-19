using AndroidOptimize.Core.Models;

namespace AndroidOptimize.App.ViewModels;

public sealed class SnapshotRowViewModel
{
    public required SnapshotSummary Summary { get; init; }
    public string Title => Summary.Snapshot.DisplayName;
    public string Subtitle =>
        $"档位 {Summary.Snapshot.Tier} · 名单 {Summary.Snapshot.ListVersion} · 序列号 {Summary.Snapshot.DeviceSerial}";
}
