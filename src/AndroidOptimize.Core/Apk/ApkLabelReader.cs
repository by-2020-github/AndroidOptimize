namespace AndroidOptimize.Core.Apk;

/// <summary>
/// 从一份完整的 APK 字节里读出应用显示名。
/// 供自检使用；真机上走 <see cref="ApkLabelSource"/>，只读需要的那几段字节。
/// </summary>
public static class ApkLabelReader
{
    /// <summary>解析失败返回 null，调用方回退到包名即可。</summary>
    public static string? ReadFromApk(byte[] apkBytes)
    {
        var reader = new ByteReader(apkBytes);
        if (reader.Length < 22) return null;

        // 末尾 64KB 里一定有 EOCD
        var tailSize = Math.Min(reader.Length, 64 * 1024);
        var tail = reader.Slice(reader.Length - tailSize, tailSize);
        var directory = ZipCentralDirectory.Locate(tail, reader.Length - tailSize, reader.Length);
        if (directory is null) return null;

        // 整包都在手里，直接把整个文件当作中央目录的来源即可（大应用的中央目录可能不在尾块里）。
        var entries = ZipCentralDirectory.Parse(apkBytes, 0, directory);
        if (entries.Count == 0) return null;

        var manifestEntry = entries.FirstOrDefault(e => e.Name == "AndroidManifest.xml");
        var arscEntry = entries.FirstOrDefault(e => e.Name == "resources.arsc");
        if (manifestEntry is null) return null;

        var manifest = ExtractEntry(apkBytes, manifestEntry);
        if (manifest is null) return null;

        var label = BinaryXml.FindApplicationLabel(manifest);
        if (label is null) return null;
        if (!label.IsReference) return Clean(label.Text);

        if (arscEntry is null) return null;
        var arsc = ExtractEntry(apkBytes, arscEntry);
        if (arsc is null) return null;

        var table = ResourceTable.Parse(arsc);
        return Clean(table?.Resolve(label.ResourceId));
    }

    private static byte[]? ExtractEntry(byte[] apkBytes, ZipCentralDirectory.Entry entry)
    {
        if (entry.LocalHeaderOffset < 0 || entry.LocalHeaderOffset >= apkBytes.Length) return null;

        var start = (int)entry.LocalHeaderOffset;
        var span = apkBytes.AsSpan(start);
        // 本地头 + 数据，长度给足（多余部分 Extract 会自己裁掉）
        var length = Math.Min(span.Length, 30 + 4096 + (int)Math.Min(entry.CompressedSize, int.MaxValue));
        if (length <= 0) return null;

        return ZipCentralDirectory.Extract(span[..length].ToArray(), entry);
    }

    /// <summary>去掉首尾空白，过长的（多半不是应用名）直接放弃。</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > 60) return null;
        return text;
    }
}
