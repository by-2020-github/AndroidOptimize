namespace AndroidOptimize.Core.Models;

/// <summary>名单里没有记录、需要进一步识别的应用。</summary>
public sealed record UnknownPackage
{
    public required string PackageName { get; init; }
    public string? Installer { get; init; }
    public string? VersionName { get; init; }
    public DateTimeOffset? FirstInstallTime { get; init; }
    public bool IsSystem { get; init; }

    public string Describe()
    {
        var parts = new List<string> { $"包名：{PackageName}" };
        if (!string.IsNullOrWhiteSpace(VersionName)) parts.Add($"版本：{VersionName}");
        if (!string.IsNullOrWhiteSpace(Installer)) parts.Add($"安装来源：{Installer}");
        if (FirstInstallTime is not null) parts.Add($"首次安装：{FirstInstallTime:yyyy-MM-dd}");
        parts.Add(IsSystem ? "系统应用" : "第三方应用");
        return string.Join(" | ", parts);
    }
}

/// <summary>识别结果（可能来自 AI，也可能来自本地缓存）。</summary>
public sealed record PackageClassification
{
    public required string PackageName { get; init; }
    public required string Category { get; init; }
    public required PackageAction Action { get; init; }
    public required double Confidence { get; init; }
    public required string Reason { get; init; }
    public string Source { get; init; } = "ai";
}
