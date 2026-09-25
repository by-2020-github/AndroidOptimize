using AndroidOptimize.Core.Adb;

namespace AndroidOptimize.Core.Apk;

/// <summary>
/// 从真机上读取某个应用在桌面上显示的名字。
///
/// 做法是只把 APK 的**局部字节**读回来（末尾的中央目录 + AndroidManifest.xml + resources.arsc），
/// 而不是 pull 整个 APK——一个应用通常只传几百 KB，整包动辄几十 MB。
/// 任何一步失败都返回 null，调用方回退到包名，不影响扫描。
/// </summary>
public class ApkLabelSource(AdbClient adb, Action<string>? log = null)
{
    private const int BlockSize = 4096;
    private const int TailBytes = 128 * 1024;

    /// <summary>单个条目最大读取量，超过就放弃（避免极少数超大资源表拖慢扫描）。</summary>
    private const int MaxEntryBytes = 32 * 1024 * 1024;

    /// <summary>
    /// 中央目录最大读取量。真机上大应用的中央目录能有 1~3 MB（条目数过万），
    /// 而尾块只有 128 KB，所以必须单独把中央目录读回来——早期版本硬要求中央目录落在尾块里，
    /// 结果 20 个应用里有 17 个读不出名字，而且因为失败不进缓存，每次扫描都重来一遍。
    /// </summary>
    private const int MaxCentralDirectoryBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan RangeTimeout = TimeSpan.FromSeconds(60);

    public virtual async Task<string?> ReadLabelAsync(string packageName, CancellationToken ct)
    {
        try
        {
            var apkPath = await GetBaseApkPathAsync(packageName, ct).ConfigureAwait(false);
            if (apkPath is null) return Fail(packageName, "找不到 base.apk（pm path 没输出）");

            var size = await GetFileSizeAsync(apkPath, ct).ConfigureAwait(false);
            if (size <= 22) return Fail(packageName, $"读不到文件大小（wc -c 返回 {size}）");

            // 1) 文件末尾一定有 ZIP 的中央目录，先把它读回来
            var tailLength = (int)Math.Min(size, TailBytes);
            var tail = await ReadRangeAsync(apkPath, size - tailLength, tailLength, ct).ConfigureAwait(false);
            if (tail.Length < 22) return Fail(packageName, $"尾块只读到 {tail.Length} 字节");

            var tailStart = size - tail.Length;
            var directory = ZipCentralDirectory.Locate(tail, tailStart, size);
            if (directory is null) return Fail(packageName, "尾块里没有可用的 ZIP 中央目录（可能是 ZIP64 或畸形包）");

            // 中央目录在尾块里就不用再跑一次 adb；不在（大应用）才单独读回来。
            byte[] catalogBytes;
            long catalogStart;
            if (directory.Offset >= tailStart && directory.Offset + directory.Size <= tailStart + tail.Length)
            {
                catalogBytes = tail;
                catalogStart = tailStart;
            }
            else
            {
                if (directory.Size > MaxCentralDirectoryBytes)
                {
                    log?.Invoke($"{packageName} 的中央目录有 {directory.Size / 1024} KB，超出上限，跳过读取应用名。");
                    return null;
                }

                catalogBytes = await ReadRangeAsync(apkPath, directory.Offset, directory.Size, ct).ConfigureAwait(false);
                catalogStart = directory.Offset;
            }

            var entries = ZipCentralDirectory.Parse(catalogBytes, catalogStart, directory);
            if (entries.Count == 0) return Fail(packageName, $"中央目录解析出 0 个条目（声明 {directory.EntryCount} 个）");

            var manifestEntry = entries.FirstOrDefault(e => e.Name == "AndroidManifest.xml");
            if (manifestEntry is null) return Fail(packageName, $"APK 里没有 AndroidManifest.xml（共 {entries.Count} 个条目）");

            // 2) 读 AndroidManifest.xml
            var manifest = await ReadEntryAsync(apkPath, manifestEntry, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                return Fail(packageName, $"读 AndroidManifest.xml 失败（压缩大小 {manifestEntry.CompressedSize} 字节）");
            }

            var label = BinaryXml.FindApplicationLabel(manifest);
            if (label is null) return Fail(packageName, "AndroidManifest.xml 里没有 android:label");
            if (!label.IsReference) return ApkLabelReader.Clean(label.Text);

            // 3) 应用名一般是 @string/app_name，得再去 resources.arsc 里查
            var arscEntry = entries.FirstOrDefault(e => e.Name == "resources.arsc");
            if (arscEntry is null) return Fail(packageName, "APK 里没有 resources.arsc");

            var arsc = await ReadEntryAsync(apkPath, arscEntry, ct).ConfigureAwait(false);
            if (arsc is null)
            {
                return Fail(packageName,
                    $"读 resources.arsc 失败（压缩后 {arscEntry.CompressedSize} 字节，上限 {MaxEntryBytes / 1024 / 1024} MB）");
            }

            var table = ResourceTable.Parse(arsc);
            var resolved = ApkLabelReader.Clean(table?.Resolve(label.ResourceId));
            return resolved ?? Fail(packageName, $"资源表里查不到 0x{label.ResourceId:x8}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(packageName, ex.Message);
        }
    }

    /// <summary>失败不是异常，但必须说出来——早期版本静默返回 null，结果「名字读不出来、又不进缓存」查了很久。</summary>
    private string? Fail(string packageName, string reason)
    {
        log?.Invoke($"读取 {packageName} 的应用名失败：{reason}");
        return null;
    }

    private async Task<string?> GetBaseApkPathAsync(string packageName, CancellationToken ct)
    {
        var result = await adb.ShellAsync($"pm path {packageName}", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

        foreach (var line in result.Lines)
        {
            var text = line.Trim();
            if (!text.StartsWith("package:", StringComparison.Ordinal)) continue;

            var path = text[8..].Trim();
            // 分片安装会有多个 apk，base.apk 才是带 AndroidManifest 的那个
            if (path.EndsWith("base.apk", StringComparison.OrdinalIgnoreCase)) return path;
        }

        // 没有 base.apk 就退回第一行
        var first = result.Lines.FirstOrDefault(l => l.TrimStart().StartsWith("package:", StringComparison.Ordinal));
        return first?[8..].Trim();
    }

    private async Task<long> GetFileSizeAsync(string apkPath, CancellationToken ct)
    {
        var result = await adb.ShellAsync($"wc -c < {Quote(apkPath)}", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return long.TryParse(result.StdOut.Trim(), out var size) ? size : -1;
    }

    private async Task<byte[]?> ReadEntryAsync(string apkPath, ZipCentralDirectory.Entry entry, CancellationToken ct)
    {
        if (entry.CompressedSize > MaxEntryBytes) return null;

        // 本地文件头最长 30 + 名字 + extra（zipalign 会塞最多 4KB 对齐填充），
        // 一次多读 4KB 就够，省掉一次往返。
        var length = (int)Math.Min(int.MaxValue, 30 + 4096 + entry.CompressedSize);
        var raw = await ReadRangeAsync(apkPath, entry.LocalHeaderOffset, length, ct).ConfigureAwait(false);
        if (raw.Length == 0) return null;

        return ZipCentralDirectory.Extract(raw, entry);
    }

    private async Task<byte[]> ReadRangeAsync(string apkPath, long offset, int length, CancellationToken ct)
    {
        if (offset < 0 || length <= 0) return [];

        // dd 只能按块跳，先算好块号，再把多读的前缀裁掉
        var firstBlock = offset / BlockSize;
        var skipBytes = (int)(offset - firstBlock * BlockSize);
        var blocks = (skipBytes + length + BlockSize - 1) / BlockSize;

        var raw = await adb.ExecOutBytesAsync(
            $"dd if={Quote(apkPath)} bs={BlockSize} skip={firstBlock} count={blocks} 2>/dev/null",
            RangeTimeout, ct).ConfigureAwait(false);

        if (raw.Length <= skipBytes) return [];

        var available = Math.Min(length, raw.Length - skipBytes);
        return raw.AsSpan(skipBytes, available).ToArray();
    }

    /// <summary>把路径包成单引号形式；路径里几乎不可能出现单引号，出现就放弃这一段。</summary>
    private static string Quote(string path)
    {
        if (path.Contains('\'')) return path;
        return $"'{path}'";
    }
}
