using System.Text.Json.Serialization;

namespace AndroidOptimize.Core.Models;

/// <summary>
/// 一台手机的档案（devices\&lt;序列号&gt;\device.json）。
/// 给多台手机做优化时靠它认出「这台来过没有、是什么机器、扫过几次」。
/// </summary>
public sealed class DeviceProfile
{
    [JsonPropertyName("serial")] public required string Serial { get; init; }
    [JsonPropertyName("brand")] public string Brand { get; set; } = string.Empty;
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("device")] public string Device { get; set; } = string.Empty;
    [JsonPropertyName("android")] public string AndroidRelease { get; set; } = string.Empty;
    [JsonPropertyName("sdk")] public int SdkInt { get; set; }
    [JsonPropertyName("rom")] public string RomName { get; set; } = string.Empty;
    [JsonPropertyName("romVersion")] public string RomVersion { get; set; } = string.Empty;

    /// <summary>累计扫到的应用数（每次扫描覆盖，取最近一次）。</summary>
    [JsonPropertyName("installedCount")] public int InstalledCount { get; set; }
    [JsonPropertyName("thirdPartyCount")] public int ThirdPartyCount { get; set; }

    [JsonPropertyName("firstSeenAt")] public DateTimeOffset FirstSeenAt { get; set; }
    [JsonPropertyName("lastSeenAt")] public DateTimeOffset LastSeenAt { get; set; }
    [JsonPropertyName("scanCount")] public int ScanCount { get; set; }
    [JsonPropertyName("executionCount")] public int ExecutionCount { get; set; }

    [JsonIgnore] public string DisplayName => $"{Brand} {Model}".Trim();
    [JsonIgnore] public string TableSummary => $"{RomName} {RomVersion}".Trim() + $" · Android {AndroidRelease}";
}

/// <summary>一次扫描的记录（devices\&lt;序列号&gt;\scans\&lt;时间&gt;.json）。</summary>
public sealed class DeviceScanRecord
{
    [JsonPropertyName("scannedAt")] public required DateTimeOffset ScannedAt { get; init; }
    [JsonPropertyName("serial")] public required string Serial { get; init; }
    [JsonPropertyName("brand")] public string Brand { get; init; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
    [JsonPropertyName("rom")] public string RomName { get; init; } = string.Empty;
    [JsonPropertyName("romVersion")] public string RomVersion { get; init; } = string.Empty;
    [JsonPropertyName("android")] public string AndroidRelease { get; init; } = string.Empty;
    [JsonPropertyName("sdk")] public int SdkInt { get; init; }
    [JsonPropertyName("listVersion")] public string ListVersion { get; init; } = string.Empty;
    [JsonPropertyName("installedCount")] public int InstalledCount { get; init; }
    [JsonPropertyName("systemCount")] public int SystemCount { get; init; }
    [JsonPropertyName("thirdPartyCount")] public int ThirdPartyCount { get; init; }
    [JsonPropertyName("detailEnriched")] public bool DetailEnriched { get; init; }
    [JsonPropertyName("packages")] public IReadOnlyList<DeviceScanPackage> Packages { get; init; } = [];
}

/// <summary>一次扫描里某个应用被观察到的事实。只记事实，不记结论——结论留给统计时现算。</summary>
public sealed class DeviceScanPackage
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>应用在桌面上显示的名字（读不到就是 null）。</summary>
    [JsonPropertyName("label")] public string? Label { get; init; }

    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("system")] public bool IsSystem { get; init; }

    /// <summary>安装来源包名。判断「是不是被商店静默装的」就靠它。</summary>
    [JsonPropertyName("installer")] public string? Installer { get; init; }

    [JsonPropertyName("hasLauncher")] public bool? HasLauncher { get; init; }
    [JsonPropertyName("neverLaunched")] public bool? NeverLaunched { get; init; }
    [JsonPropertyName("firstInstallTime")] public DateTimeOffset? FirstInstallTime { get; init; }

    /// <summary>界面上的「建议」文案，和列表里看到的是同一句。</summary>
    [JsonPropertyName("advice")] public string? Advice { get; init; }

    /// <summary>离线名册的结论（recommended / advanced / expert / unsafe）。</summary>
    [JsonPropertyName("removal")] public string? Removal { get; init; }

    /// <summary>名单里有没有这个包（有则记中文名与动作）。</summary>
    [JsonPropertyName("ruleName")] public string? RuleName { get; init; }
    [JsonPropertyName("ruleAction")] public string? RuleAction { get; init; }

    /// <summary>受保护名单命中（受保护的不会被处理，但记下来能看出保护名单覆盖得够不够）。</summary>
    [JsonPropertyName("protection")] public string? Protection { get; init; }

    /// <summary>申请的全部权限（只有深度扫描到的应用才有）。用来发现名单里还没收录的可疑权限。</summary>
    [JsonPropertyName("permissions")] public IReadOnlyList<string>? Permissions { get; init; }
}

/// <summary>一次执行优化的记录（devices\&lt;序列号&gt;\executions\&lt;时间&gt;.json）。</summary>
public sealed class DeviceExecutionRecord
{
    [JsonPropertyName("executedAt")] public required DateTimeOffset ExecutedAt { get; init; }
    [JsonPropertyName("serial")] public required string Serial { get; init; }
    [JsonPropertyName("tier")] public string Tier { get; init; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; init; } = string.Empty;
    [JsonPropertyName("actions")] public IReadOnlyList<DeviceExecutionAction> Actions { get; init; } = [];
}

public sealed class DeviceExecutionAction
{
    [JsonPropertyName("target")] public required string Target { get; init; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    [JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("verified")] public bool Verified { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}
