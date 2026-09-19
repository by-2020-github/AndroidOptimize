using System.Reflection;
using System.Security.Cryptography;

namespace AndroidOptimize.Core.Adb;

/// <summary>
/// 把随程序打包的 adb 释放到用户数据目录。
/// 这样发布物可以是单个 exe，而 adb 本身需要是磁盘上的可执行文件才能运行。
/// </summary>
public static class AdbBootstrap
{
    private static readonly string[] RequiredFiles = ["adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll"];

    /// <summary>
    /// 释放内嵌的 adb。已经存在且内容一致时不会重复写盘；
    /// 返回 adb.exe 的路径，找不到内嵌资源时返回 null。
    /// </summary>
    public static string? Ensure(Action<string>? log = null)
    {
        var targetDirectory = Services.AppPaths.PlatformToolsDir;
        var adbPath = Path.Combine(targetDirectory, "adb.exe");

        if (!HasEmbeddedAdb())
        {
            log?.Invoke("程序中未内嵌 adb，将使用程序目录或系统 PATH 里的 adb。");
            return File.Exists(adbPath) ? adbPath : null;
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex)
        {
            log?.Invoke($"无法创建目录 {targetDirectory}：{ex.Message}");
            return File.Exists(adbPath) ? adbPath : null;
        }

        var extracted = false;
        foreach (var name in RequiredFiles)
        {
            var bytes = ReadEmbedded(name);
            if (bytes is null) continue;

            var destination = Path.Combine(targetDirectory, name);
            if (IsSameContent(destination, bytes)) continue;

            try
            {
                WriteAtomically(destination, bytes);
                extracted = true;
            }
            catch (Exception ex)
            {
                // adb 正在运行时文件会被占用，此时沿用已有文件即可。
                log?.Invoke($"释放 {name} 失败（{ex.Message}），将使用已有文件。");
            }
        }

        if (extracted)
        {
            log?.Invoke($"已释放内置 adb 到 {targetDirectory}");
        }

        return File.Exists(adbPath) ? adbPath : null;
    }

    public static bool HasEmbeddedAdb() => ReadEmbedded("adb.exe") is not null;

    private static byte[]? ReadEmbedded(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool IsSameContent(string path, byte[] expected)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expected.LongLength) return false;

            using var stream = File.OpenRead(path);
            var actual = SHA256.HashData(stream);
            var wanted = SHA256.HashData(expected);
            return actual.AsSpan().SequenceEqual(wanted);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAtomically(string destination, byte[] bytes)
    {
        var temp = destination + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, destination, overwrite: true);
    }
}
