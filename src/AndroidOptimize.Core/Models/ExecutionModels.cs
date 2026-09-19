namespace AndroidOptimize.Core.Models;

public sealed record ExecutionProgress(int Index, int Total, string Message, PlanItem? Item = null);

public sealed record ExecutionItemResult
{
    public required PlanItem Item { get; init; }
    public required ActionStatus Status { get; init; }
    public required string Message { get; init; }
    public string? RawOutput { get; init; }
    public string? Hint { get; init; }
    public bool Verified { get; init; }
    public string? VerifyDetail { get; init; }

    public string StatusText => Status switch
    {
        ActionStatus.Success => "成功",
        ActionStatus.Downgraded => "已降级成功",
        ActionStatus.Skipped => "已跳过",
        ActionStatus.Failed => "失败",
        _ => "未执行",
    };
}

public sealed class ExecutionReport
{
    public required DeviceInfo Device { get; init; }
    public required OptimizationTier Tier { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required IReadOnlyList<ExecutionItemResult> Results { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public SnapshotFile? Snapshot { get; init; }
    public string? SnapshotPath { get; init; }
    public string? LogPath { get; init; }

    public int SuccessCount => Results.Count(r => r.Status is ActionStatus.Success);
    public int DowngradedCount => Results.Count(r => r.Status is ActionStatus.Downgraded);
    public int FailedCount => Results.Count(r => r.Status is ActionStatus.Failed);
    public int SkippedCount => Results.Count(r => r.Status is ActionStatus.Skipped);
    public int VerifiedCount => Results.Count(r => r.Verified);
    public TimeSpan Duration => FinishedAt - StartedAt;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (SuccessCount > 0) parts.Add($"成功 {SuccessCount}");
            if (DowngradedCount > 0) parts.Add($"降级成功 {DowngradedCount}");
            if (SkippedCount > 0) parts.Add($"跳过 {SkippedCount}");
            if (FailedCount > 0) parts.Add($"失败 {FailedCount}");
            return parts.Count == 0 ? "没有执行任何操作" : string.Join(" · ", parts);
        }
    }
}

public sealed record RollbackItemResult(string Target, string DisplayName, bool Success, string Message);

public sealed class RollbackReport
{
    public required string SnapshotPath { get; init; }
    public required IReadOnlyList<RollbackItemResult> Results { get; init; }
    public int SuccessCount => Results.Count(r => r.Success);
    public int FailedCount => Results.Count(r => !r.Success);
}
