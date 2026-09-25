namespace AndroidOptimize.Core.Apk;

/// <summary>
/// 读取 AndroidManifest.xml（二进制 AXML），只做一件事：
/// 找出 &lt;application android:label="..."&gt; 的取值（字符串或资源 ID）。
/// </summary>
internal static class BinaryXml
{
    private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    public sealed record LabelValue(bool IsReference, string? Text, uint ResourceId);

    public static LabelValue? FindApplicationLabel(byte[] manifest)
    {
        var reader = new ByteReader(manifest);
        if (reader.Length < 8 || reader.UInt16(0) != ChunkTypes.Xml) return null;

        var pool = StringPool.Parse(reader, 8, out _);
        if (pool is null) return null;

        var p = 8;
        while (p + 8 <= reader.Length)
        {
            var header = new ChunkHeader(reader.UInt16(p), reader.UInt16(p + 2), reader.UInt32(p + 4));
            if (!header.IsValid || header.Size > reader.Length - p) break;

            if (header.Type == ChunkTypes.XmlStartElement)
            {
                var value = ReadStartElement(reader, p, header, pool);
                if (value is not null) return value;
            }

            p += (int)header.Size;
        }

        return null;
    }

    private static LabelValue? ReadStartElement(ByteReader reader, int chunkStart, ChunkHeader header, StringPool pool)
    {
        // START_ELEMENT 固定头之后：lineNumber(4) comment(4) ns(4) name(4)
        //                         attributeStart(2) attributeSize(2) attributeCount(2) idIndex(2) classIndex(2) styleIndex(2)
        if (!reader.InRange(chunkStart + 16, 20)) return null;

        var nameIndex = reader.UInt32(chunkStart + 20);
        if (pool.At((int)nameIndex) != "application") return null;

        var attributeStart = reader.UInt16(chunkStart + 24);
        var attributeSize = reader.UInt16(chunkStart + 26);
        var attributeCount = reader.UInt16(chunkStart + 28);
        if (attributeSize < 20 || attributeCount > 1000) return null;

        var baseOffset = chunkStart + 16 + attributeStart;
        for (var i = 0; i < attributeCount; i++)
        {
            var a = baseOffset + i * attributeSize;
            if (!reader.InRange(a, 20)) return null;

            var namespaceIndex = reader.UInt32(a);
            var attributeNameIndex = reader.UInt32(a + 4);
            if (pool.At((int)attributeNameIndex) != "label") continue;

            // 只要 android 命名空间下的 label（自定义命名空间的 label 不是应用名）
            var ns = pool.At((int)namespaceIndex);
            if (!string.IsNullOrEmpty(ns) && ns != AndroidNamespace) continue;

            var dataType = reader.Byte(a + 15);
            var data = reader.UInt32(a + 16);

            return dataType switch
            {
                ValueTypes.String => new LabelValue(false, pool.At((int)data), 0),
                ValueTypes.Reference => new LabelValue(true, null, data),
                // 也见过把字符串写在 rawValue 里的：兜底用 raw 索引
                _ => new LabelValue(false, pool.At((int)reader.UInt32(a + 8)), 0),
            };
        }

        return null;
    }
}
