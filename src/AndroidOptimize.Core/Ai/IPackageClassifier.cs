using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Ai;

public interface IPackageClassifier
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<PackageClassification>> ClassifyAsync(
        IReadOnlyList<UnknownPackage> packages,
        DeviceInfo device,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
