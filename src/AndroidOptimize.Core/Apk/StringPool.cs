using System.Text;

namespace AndroidOptimize.Core.Apk;

/// <summary>
/// AXML / ARSC 里的字符串池（ResStringPool）。
/// 字符串有两种编码：UTF-8（长度用 1~2 字节变长整数）和 UTF-16（长度是 uint16）。
/// </summary>
internal sealed class StringPool
{
    private StringPool(IReadOnlyList<string?> strings) => Strings = strings;

    public IReadOnlyList<string?> Strings { get; }
    public int Count => Strings.Count;

    public string? At(int index) => index >= 0 && index < Strings.Count ? Strings[index] : null;

    public static StringPool? Parse(ByteReader reader, int offset, out int endOffset)
    {
        endOffset = offset;
        if (!reader.InRange(offset, 28)) return null;

        var header = new ChunkHeader(reader.UInt16(offset), reader.UInt16(offset + 2), reader.UInt32(offset + 4));
        if (header.Type != ChunkTypes.StringPool || !header.IsValid) return null;

        var stringCount = reader.UInt32(offset + 8);
        var flags = reader.UInt32(offset + 16);
        var stringsStart = reader.UInt32(offset + 20);
        var isUtf8 = (flags & 0x100) != 0;

        endOffset = offset + (int)header.Size;

        // 上限保护：畸形数据可能导致 stringCount 是天文数字
        if (stringCount > 500_000) return null;
        if (!reader.InRange(offset + header.HeaderSize, (int)stringCount * 4)) return null;

        var strings = new string?[stringCount];
        var dataBase = offset + (int)stringsStart;

        for (var i = 0; i < stringCount; i++)
        {
            var stringOffset = reader.UInt32(offset + header.HeaderSize + i * 4);
            var p = dataBase + (int)stringOffset;
            strings[i] = isUtf8 ? ReadUtf8(reader, ref p) : ReadUtf16(reader, ref p);
        }

        return new StringPool(strings);
    }

    private static string? ReadUtf8(ByteReader reader, ref int p)
    {
        var charCount = ReadVarInt(reader, ref p);
        if (charCount < 0) return null;

        var byteCount = ReadVarInt(reader, ref p);
        if (byteCount < 0 || !reader.InRange(p, byteCount)) return null;

        var text = Encoding.UTF8.GetString(reader.Data, p, byteCount);
        p += byteCount;
        return text;
    }

    private static string? ReadUtf16(ByteReader reader, ref int p)
    {
        var length = (int)reader.UInt16(p);
        p += 2;

        // 最高位为 1 表示长字符串，真正的长度在后面再读一个 uint16
        if ((length & 0x8000) != 0)
        {
            length = ((length & 0x7FFF) << 16) | reader.UInt16(p);
            p += 2;
        }

        if (length < 0 || !reader.InRange(p, length * 2)) return null;

        var text = Encoding.Unicode.GetString(reader.Data, p, length * 2);
        p += length * 2;
        return text;
    }

    /// <summary>UTF-8 字符串长度用的变长整数：最高位表示还有后续字节。</summary>
    private static int ReadVarInt(ByteReader reader, ref int p)
    {
        if (!reader.InRange(p, 1)) return -1;

        int value = reader.Byte(p);
        p++;
        if ((value & 0x80) != 0)
        {
            if (!reader.InRange(p, 1)) return -1;
            value = ((value & 0x7F) << 8) | reader.Byte(p);
            p++;
        }
        return value;
    }
}
