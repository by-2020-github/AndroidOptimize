using System.IO.Compression;

namespace AndroidOptimize.Core.Apk;

/// <summary>
/// 只解析 ZIP 的中央目录，拿到某个条目的偏移与长度。
/// 用途：让设备端只需要把 APK 的局部区间读回来，而不是 pull 整个包。
/// </summary>
internal static class ZipCentralDirectory
{
    private const uint EndOfCentralDirectory = 0x06054b50;
    private const uint CentralFileHeader = 0x02014b50;

    public sealed record Entry(string Name, long LocalHeaderOffset, long CompressedSize, int CompressionMethod);

    /// <summary>
    /// 中央目录的位置与大小。由尾块里的 EOCD 得到，**不代表这段字节已经在手上**：
    /// 大应用（一万多个条目）的中央目录能有 1~3 MB，远超我们读回来的尾块。
    /// </summary>
    public sealed record Directory(long Offset, int Size, int EntryCount);

    /// <summary>
    /// 从「文件尾部的一段字节」里找到 EOCD，得到中央目录的位置与大小。
    /// tailStart 是这段字节在原文件中的起始偏移。
    /// </summary>
    public static Directory? Locate(byte[] tail, long tailStart, long fileSize)
    {
        var reader = new ByteReader(tail);

        // 1) 找 EOCD（从后往前找，注释最长 64KB，我们只读了一段所以够用）
        var eocd = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (reader.UInt32(i) == EndOfCentralDirectory)
            {
                eocd = i;
                break;
            }
        }
        if (eocd < 0) return null;

        var entryCount = reader.UInt16(eocd + 10);
        var centralSize = reader.UInt32(eocd + 12);
        var centralOffset = reader.UInt32(eocd + 16);

        // ZIP64 或把偏移写进注释的畸形包，这里直接放弃（回退到包名即可）。
        if (entryCount == 0xFFFF || centralSize == 0xFFFFFFFF || centralOffset == 0xFFFFFFFF) return null;
        if (centralSize == 0 || centralOffset + centralSize > fileSize) return null;

        return new Directory(centralOffset, (int)centralSize, entryCount);
    }

    /// <summary>
    /// 解析中央目录。buffer 必须完整包含中央目录，bufferStart 是它在原文件中的起始偏移。
    /// </summary>
    public static IReadOnlyList<Entry> Parse(byte[] buffer, long bufferStart, Directory directory)
    {
        var reader = new ByteReader(buffer);

        // 中央目录必须完整落在我们读回来的这段字节里
        var localStart = directory.Offset - bufferStart;
        if (localStart < 0 || localStart + directory.Size > buffer.Length) return [];

        var entries = new List<Entry>(directory.EntryCount);
        var p = (int)localStart;
        for (var i = 0; i < directory.EntryCount; i++)
        {
            if (!reader.InRange(p, 46) || reader.UInt32(p) != CentralFileHeader) break;

            var method = reader.UInt16(p + 10);
            var compressedSize = reader.UInt32(p + 20);
            var nameLength = reader.UInt16(p + 28);
            var extraLength = reader.UInt16(p + 30);
            var commentLength = reader.UInt16(p + 32);
            var localOffset = reader.UInt32(p + 42);

            if (!reader.InRange(p + 46, nameLength)) break;
            var name = System.Text.Encoding.UTF8.GetString(buffer, p + 46, nameLength);

            entries.Add(new Entry(name, localOffset, compressedSize, method));
            p += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }

    /// <summary>
    /// 把「本地文件头 + 压缩数据」这段字节解出来。
    /// 调用方负责按本地头里的偏移裁掉多余的前缀字节。
    /// </summary>
    public static byte[]? Extract(byte[] localHeaderAndData, Entry entry)
    {
        var reader = new ByteReader(localHeaderAndData);
        if (reader.Length < 30 || reader.UInt32(0) != 0x04034b50) return null;

        var method = reader.UInt16(8);
        var compressedSize = reader.UInt32(18);

        // 位 3 置位时大小写在数据描述符里，本地头里是 0，这种情况只能用中央目录里的值。
        if (compressedSize == 0) compressedSize = (uint)entry.CompressedSize;
        if (compressedSize > localHeaderAndData.Length) return null;

        var nameLength = reader.UInt16(26);
        var extraLength = reader.UInt16(28);
        var dataStart = 30 + nameLength + extraLength;
        if (dataStart + compressedSize > localHeaderAndData.Length) return null;

        var payload = localHeaderAndData.AsSpan(dataStart, (int)compressedSize).ToArray();

        if (method == 0) return payload;              // 未压缩
        if (method != 8) return null;                 // 只支持 deflate

        try
        {
            using var input = new MemoryStream(payload);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>本地头长度（用于计算要向设备多读多少前缀字节）。</summary>
    public static int LocalHeaderLength(byte[] localHeader)
    {
        var reader = new ByteReader(localHeader);
        if (reader.Length < 30) return 30;
        return 30 + reader.UInt16(26) + reader.UInt16(28);
    }
}
