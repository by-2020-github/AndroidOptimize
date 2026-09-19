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
    public static string UserDataDir => Path.Combine(UserRoot, "data");
    public static string SettingsFile => Path.Combine(UserRoot, "settings.json");
    public static string AiCacheFile => Path.Combine(UserRoot, "ai-cache.json");
    public static string PlatformToolsDir => Path.Combine(UserRoot, "platform-tools");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(UserRoot);
        Directory.CreateDirectory(SnapshotsDir);
        Directory.CreateDirectory(ReportsDir);
        Directory.CreateDirectory(LogsDir);
    }

    public static string NewSnapshotPath(string deviceLabel)
    {
        EnsureCreated();
        var safeLabel = Sanitize(deviceLabel);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeLabel}.json";
        return Path.Combine(SnapshotsDir, name);
    }

    public static string NewReportPath(string deviceLabel, string extension = "md")
    {
        EnsureCreated();
        var safeLabel = Sanitize(deviceLabel);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-优化报告-{safeLabel}.{extension}";
        return Path.Combine(ReportsDir, name);
    }

    public static string NewLogPath(string prefix = "run")
    {
        EnsureCreated();
        return Path.Combine(LogsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{prefix}.log");
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "device";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var result = new string(chars);
        return result.Length > 40 ? result[..40] : result;
    }
}
