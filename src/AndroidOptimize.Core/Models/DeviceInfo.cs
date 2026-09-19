namespace AndroidOptimize.Core.Models;

public sealed record DeviceInfo
{
    public required string Serial { get; init; }
    public required string Brand { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public required string Device { get; init; }
    public required string AndroidRelease { get; init; }
    public required int SdkInt { get; init; }
    public required string BuildId { get; init; }
    public required string RomName { get; init; }
    public required string RomVersion { get; init; }
    public required string CpuAbi { get; init; }

    /// <summary>用于匹配名单里 brands 字段的令牌集合（品牌、厂商、子品牌）。</summary>
    public required IReadOnlyList<string> BrandTokens { get; init; }

    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Manufacturer)) parts.Add(Manufacturer);
            if (!string.IsNullOrWhiteSpace(Model)) parts.Add(Model);
            var name = string.Join(' ', parts).Trim();
            return string.IsNullOrWhiteSpace(name) ? Serial : name;
        }
    }

    public string SystemSummary
    {
        get
        {
            var rom = string.Join(' ', new[] { RomName, RomVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(rom)) parts.Add(rom.Trim());
            parts.Add($"Android {AndroidRelease} (API {SdkInt})");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record AdbDevice
{
    public required string Serial { get; init; }
    public required string State { get; init; }
    public string? Model { get; init; }
    public string? Product { get; init; }
    public string? TransportId { get; init; }

    public bool IsUsable => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
    public bool IsUnauthorized => string.Equals(State, "unauthorized", StringComparison.OrdinalIgnoreCase);
    public bool IsOffline => string.Equals(State, "offline", StringComparison.OrdinalIgnoreCase);
}
