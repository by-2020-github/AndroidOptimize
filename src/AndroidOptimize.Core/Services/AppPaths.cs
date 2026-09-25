namespace AndroidOptimize.Core.Services;

/// <summary>程序运行期的各类文件位置。所有用户数据都放在 %LOCALAPPDATA%\AndroidOptimize 下，方便一键清理。</summary>
public static class AppPaths
{
    public const string ProductName = "AndroidOptimize";

    public static string UserRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    public static string SnapshotsDir => Path.Combine(UserRoot, "snapshots");
    public static string ReportsDir => Path.Combine(UserRoot, "reports");
    public static string LogsDir => Path.Combine(UserRoot, "logs");
    public static string DevicesDir => Path.Combine(UserRoot, "devices");
    public static string UserDataDir => Path.Combine(UserRoot, "data");
    /// <summary>
    /// 设置文件位置。截图/演示流程会把它指到临时目录，
    /// 免得生成的文档截图上带着开发者本机改过的开关（默认状态才对读者有意义）。
    /// </summary>
    public static string? SettingsPathOverride { get; set; }

    public static string SettingsFile => SettingsPathOverride ?? Path.Combine(UserRoot, "settings.json");
    public static string AiCacheFile => Path.Combine(UserRoot, "ai-cache.json");
    public static string LabelCacheFile => Path.Combine(UserRoot, "label-cache.json");
    /// <summary>安装来源与权限列表的缓存（key = 包名@版本号）。</summary>
    public static string DetailCacheFile => Path.Combine(UserRoot, "detail-cache.json");
    public static string PlatformToolsDir => Path.Combine(UserRoot, "platform-tools");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(UserRoot);
        Directory.CreateDirectory(SnapshotsDir);
        Directory.CreateDirectory(ReportsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(DevicesDir);
    }

    /// <summary>每台手机一个目录，按序列号分：devices\&lt;序列号&gt;\。</summary>
    public static string DeviceDirectory(string serial) =>
        Path.Combine(DevicesDir, Sanitize(string.IsNullOrWhiteSpace(serial) ? "未知设备" : serial));

    public static string DeviceScansDir(string serial) => Path.Combine(DeviceDirectory(serial), "scans");
    public static string DeviceExecutionsDir(string serial) => Path.Combine(DeviceDirectory(serial), "executions");
    public static string DeviceSnapshotsDir(string serial) => Path.Combine(DeviceDirectory(serial), "snapshots");
    public static string DeviceReportsDir(string serial) => Path.Combine(DeviceDirectory(serial), "reports");

    public static string NewSnapshotPath(string deviceLabel, string? serial = null)
    {
        EnsureCreated();
        var safeLabel = Sanitize(deviceLabel);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeLabel}.json";
        if (string.IsNullOrWhiteSpace(serial)) return Path.Combine(SnapshotsDir, name);

        var directory = DeviceSnapshotsDir(serial);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    public static string NewReportPath(string deviceLabel, string extension = "md", string? serial = null)
    {
        EnsureCreated();
        var safeLabel = Sanitize(deviceLabel);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-优化报告-{safeLabel}.{extension}";
        if (string.IsNullOrWhiteSpace(serial)) return Path.Combine(ReportsDir, name);

        var directory = DeviceReportsDir(serial);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    public static string NewLogPath(string prefix = "run")
    {
        EnsureCreated();
        return Path.Combine(LogsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{prefix}.log");
    }

    /// <summary>把字符串变成安全的目录名（序列号、设备名都可能带奇怪字符）。</summary>
    internal static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "device";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var result = new string(chars);
        return result.Length > 40 ? result[..40] : result;
    }
}
