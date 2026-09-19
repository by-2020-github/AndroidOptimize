namespace AndroidOptimize.Core.Adb;

public sealed record AdbLocation(string Path, string Source);

public static class AdbLocator
{
    /// <summary>
    /// 依次尝试：显式指定 → 环境变量 → 用户数据目录（未来在线下载）→ 程序目录 bin → 向上查找仓库 bin → PATH → 常见 SDK 路径。
    /// </summary>
    public static AdbLocation? Locate(string? explicitPath = null, string? appBaseDirectory = null)
    {
        foreach (var (path, source) in Candidates(explicitPath, appBaseDirectory))
        {
            if (File.Exists(path))
            {
                return new AdbLocation(Path.GetFullPath(path), source);
            }
        }

        var onPath = FindOnPath();
        if (onPath is not null)
        {
            return new AdbLocation(onPath, "系统 PATH");
        }

        return null;
    }

    public static IEnumerable<(string Path, string Source)> Candidates(string? explicitPath = null, string? appBaseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return (explicitPath!, "显式指定");
        }

        var fromEnv = Environment.GetEnvironmentVariable("ANDROIDOPTIMIZE_ADB");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            yield return (fromEnv!, "环境变量 ANDROIDOPTIMIZE_ADB");
        }

        yield return (Path.Combine(Services.AppPaths.PlatformToolsDir, "adb.exe"), "用户数据目录 platform-tools");

        if (!string.IsNullOrWhiteSpace(appBaseDirectory))
        {
            yield return (Path.Combine(appBaseDirectory!, "bin", "adb.exe"), "程序目录 bin");
            yield return (Path.Combine(appBaseDirectory!, "platform-tools", "adb.exe"), "程序目录 platform-tools");
            yield return (Path.Combine(appBaseDirectory!, "vendor", "platform-tools", "adb.exe"), "程序目录 vendor/platform-tools");

            // 开发环境：从输出目录逐级向上找仓库里的 adb（vendor\platform-tools 是当前布局，bin 是旧布局）
            var dir = new DirectoryInfo(appBaseDirectory!);
            for (var depth = 0; depth < 7 && dir is not null; depth++, dir = dir.Parent)
            {
                yield return (Path.Combine(dir.FullName, "vendor", "platform-tools", "adb.exe"), $"向上查找 ({depth}) vendor");
                yield return (Path.Combine(dir.FullName, "bin", "adb.exe"), $"向上查找 ({depth}) bin");
                yield return (Path.Combine(dir.FullName, "platform-tools", "adb.exe"), $"向上查找 ({depth}) platform-tools");
            }
        }

        yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"), "Android SDK");
        yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "platform-tools", "adb.exe"), "Program Files\\Android");
        yield return (@"C:\platform-tools\adb.exe", "C:\\platform-tools");
        yield return (@"D:\platform-tools\adb.exe", "D:\\platform-tools");
    }

    private static string? FindOnPath()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable)) return null;

        foreach (var folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), "adb.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch
            {
                // 忽略 PATH 中的非法路径。
            }
        }

        return null;
    }
}
