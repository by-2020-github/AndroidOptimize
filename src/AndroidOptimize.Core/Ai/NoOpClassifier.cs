using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Ai;

/// <summary>未启用 AI 时使用，保证调用方不需要判空。</summary>
public sealed class NoOpClassifier : IPackageClassifier
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<PackageClassification>> ClassifyAsync(
        IReadOnlyList<UnknownPackage> packages,
        DeviceInfo device,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PackageClassification>>([]);
}
