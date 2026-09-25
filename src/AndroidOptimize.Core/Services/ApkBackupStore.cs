using AndroidOptimize.Core.Adb;

namespace AndroidOptimize.Core.Services;

/// <summary>
/// 卸载前把安装包备份到电脑上，卸载之后还能装回来。
///
/// **为什么必须有这个**：`pm uninstall --user 0` 对商店安装的应用（APK 在 /data/app）
/// 会把安装包一起删掉，`cmd package install-existing` 只能救回系统分区上还有副本的应用
/// （系统预装应用、系统应用的更新版本）。真机上验证过：卸载后 /data/app 里一个 apk 都不剩，
/// 连数据目录也一起清掉了——这类应用卸载了就真的没了。
///
/// 所以对「不是系统预装」的应用，卸载前先把 base.apk 和各个 split 拉到
/// devices\&lt;序列号&gt;\apk-backup\&lt;包名&gt;\ 下，回滚时用 install-multiple 装回去。
/// </summary>
public static class ApkBackupStore
{
    public static string DirectoryFor(string serial, string packageName, string? devicesRoot = null) =>
        Path.Combine(devicesRoot ?? AppPaths.DevicesDir, AppPaths.Sanitize(serial), "apk-backup",
            AppPaths.Sanitize(packageName));

    /// <summary>这台电脑上有没有这个应用的备份。</summary>
    public static IReadOnlyList<string> Find(string serial, string packageName, string? devicesRoot = null)
    {
        var directory = DirectoryFor(serial, packageName, devicesRoot);
        if (!Directory.Exists(directory)) return [];

        return Directory.EnumerateFiles(directory, "*.apk")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static long SizeOf(IReadOnlyList<string> files) =>
        files.Sum(f => new FileInfo(f).Exists ? new FileInfo(f).Length : 0);

    /// <summary>把应用的全部安装包（base + split）拉到电脑上。</summary>
    public static async Task<(bool Ok, string Message, IReadOnlyList<string> Files)> CreateAsync(
        AdbClient adb,
        string serial,
        string packageName,
        string? devicesRoot = null,
        CancellationToken ct = default)
    {
        var result = await adb.ShellAsync($"pm path {packageName}", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var remotePaths = result.Lines
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.Ordinal))
            .Select(line => line[8..].Trim())
            .Where(path => path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (remotePaths.Count == 0)
        {
            return (false, "拿不到安装包路径（pm path 没有输出），这个应用卸载后无法恢复。", []);
        }

        var directory = DirectoryFor(serial, packageName, devicesRoot);
        Directory.CreateDirectory(directory);
        var files = new List<string>();

        foreach (var remote in remotePaths)
        {
            var local = Path.Combine(directory, Path.GetFileName(remote));
            var pull = await adb.RunAsync(["pull", remote, local], TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);

            if (!File.Exists(local) || new FileInfo(local).Length == 0)
            {
                return (false, $"备份 {Path.GetFileName(remote)} 失败：{pull.Combined.Trim()}", files);
            }

            files.Add(local);
        }

        var size = SizeOf(files);
        return (true, $"已备份 {files.Count} 个安装包（{size / 1024.0 / 1024:0.0} MB）", files);
    }

    /// <summary>用电脑上的备份把应用装回去。</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(
        AdbClient adb,
        IReadOnlyList<string> files,
        CancellationToken ct = default)
    {
        if (files.Count == 0) return (false, "没有可用的备份文件。");

        var arguments = new List<string> { files.Count == 1 ? "install" : "install-multiple", "-r" };
        arguments.AddRange(files);

        var result = await adb.RunAsync(arguments, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        var ok = result.Ok && result.Combined.Contains("Success", StringComparison.OrdinalIgnoreCase);

        return ok
            ? (true, $"已用电脑上的备份装回（{files.Count} 个安装包）。")
            : (false, $"用备份安装失败：{result.Combined.Trim()}");
    }
}
